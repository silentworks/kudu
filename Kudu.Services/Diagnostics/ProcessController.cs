﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Diagnostics;
using Kudu.Core.Infrastructure;
using Kudu.Services.Infrastructure;

namespace Kudu.Services.Performance
{
    public class ProcessController : ApiController
    {
        private const string FreeSitePolicy = "Shared|Limited";

        private readonly ITracer _tracer;
        private readonly IEnvironment _environment;
        private readonly IDeploymentSettingsManager _settings;

        public ProcessController(ITracer tracer,
                                 IEnvironment environment,
                                 IDeploymentSettingsManager settings)
        {
            _tracer = tracer;
            _environment = environment;
            _settings = settings;
        }

        [HttpGet]
        public HttpResponseMessage GetOpenFiles(int processId)
        {
            using (_tracer.Step("ProcessController.GetOpenFiles"))
            {
                return Request.CreateResponse(HttpStatusCode.OK, GetOpenFileHandles(processId));
            }
        }

        [HttpGet]
        public HttpResponseMessage GetThread(int processId, int threadId)
        {
            using (_tracer.Step("ProcessController.GetThread"))
            {
                var process = GetProcessById(processId);
                var thread = process.Threads.Cast<ProcessThread>().FirstOrDefault(t => t.Id == threadId);

                if (thread != null)
                {
                    return Request.CreateResponse(HttpStatusCode.OK, GetProcessThreadInfo(thread, Request.RequestUri.AbsoluteUri, true));
                }
                else
                {
                    return Request.CreateResponse(HttpStatusCode.NotFound);
                }
            }
        }

        [HttpGet]
        public HttpResponseMessage GetAllThreads(int id)
        {
            using (_tracer.Step("ProcessController.GetAllThreads"))
            {
                var process = GetProcessById(id);
                var results = new List<ProcessThreadInfo>();

                foreach (ProcessThread thread in process.Threads)
                {
                    results.Add(GetProcessThreadInfo(thread, Request.RequestUri.AbsoluteUri.TrimEnd('/') + '/' + thread.Id, false));
                }

                return Request.CreateResponse(HttpStatusCode.OK, results);
            }
        }

        [HttpGet]
        public HttpResponseMessage GetAllProcesses()
        {
            using (_tracer.Step("ProcessController.GetAllProcesses"))
            {
                var results = Process.GetProcesses().Select(p => GetProcessInfo(p, Request.RequestUri.AbsoluteUri.TrimEnd('/') + '/' + p.Id)).OrderBy(p => p.Name.ToLowerInvariant()).ToList();
                return Request.CreateResponse(HttpStatusCode.OK, results);
            }
        }

        [HttpGet]
        public HttpResponseMessage GetProcess(int id)
        {
            using (_tracer.Step("ProcessController.GetProcess"))
            {
                var process = GetProcessById(id);
                return Request.CreateResponse(HttpStatusCode.OK, GetProcessInfo(process, Request.RequestUri.AbsoluteUri, details: true));
            }
        }

        [HttpDelete]
        public void KillProcess(int id)
        {
            using (_tracer.Step("ProcessController.KillProcess"))
            {
                var process = GetProcessById(id);
                process.Kill(includesChildren: true, tracer: _tracer);
            }
        }

        [HttpGet]
        public HttpResponseMessage MiniDump(int id, int dumpType = 0, string format = null)
        {
            using (_tracer.Step("ProcessController.MiniDump"))
            {
                DumpFormat dumpFormat = ParseDumpFormat(format, DumpFormat.Raw);
                if (dumpFormat != DumpFormat.Raw && dumpFormat != DumpFormat.Zip)
                {
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest,
                        String.Format(CultureInfo.CurrentCulture, Resources.Error_DumpFormatNotSupported, dumpFormat));
                }

                string sitePolicy = _settings.GetWebSitePolicy();
                if ((MINIDUMP_TYPE)dumpType == MINIDUMP_TYPE.WithFullMemory && sitePolicy.Equals(FreeSitePolicy, StringComparison.OrdinalIgnoreCase))
                {
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError,
                        String.Format(CultureInfo.CurrentCulture, Resources.Error_FullMiniDumpNotSupported, sitePolicy));
                }

                var process = GetProcessById(id);

                string dumpFile = Path.Combine(_environment.LogFilesPath, "minidump", "minidump.dmp");
                FileSystemHelpers.EnsureDirectory(Path.GetDirectoryName(dumpFile));
                FileSystemHelpers.DeleteFileSafe(dumpFile);

                try
                {
                    using (_tracer.Step(String.Format("MiniDump pid={0}, name={1}, file={2}", process.Id, process.ProcessName, dumpFile)))
                    {
                        process.MiniDump(dumpFile, (MINIDUMP_TYPE)dumpType);
                        _tracer.Trace("MiniDump size={0}", new FileInfo(dumpFile).Length);
                    }
                }
                catch (Exception ex)
                {
                    _tracer.TraceError(ex);
                    FileSystemHelpers.DeleteFileSafe(dumpFile);
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex.Message);
                }

                if (dumpFormat == DumpFormat.Raw)
                {
                    string responseFileName = GetResponseFileName(process.ProcessName, "dmp");

                    HttpResponseMessage response = Request.CreateResponse();
                    response.Content = new StreamContent(FileStreamWrapper.OpenRead(dumpFile));
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment");
                    response.Content.Headers.ContentDisposition.FileName = responseFileName;
                    return response;
                }
                else if (dumpFormat == DumpFormat.Zip)
                {
                    string responseFileName = GetResponseFileName(process.ProcessName, "zip");

                    HttpResponseMessage response = Request.CreateResponse();
                    response.Content = ZipStreamContent.Create(responseFileName, _tracer, zip =>
                    {
                        try
                        {
                            zip.AddFile(dumpFile, _tracer, String.Empty);
                        }
                        finally
                        {
                            FileSystemHelpers.DeleteFileSafe(dumpFile);
                        }

                        foreach (var fileName in new[] { "sos.dll", "mscordacwks.dll" })
                        {
                            string filePath = Path.Combine(ProcessExtensions.ClrRuntimeDirectory, fileName);
                            if (FileSystemHelpers.Instance.File.Exists(filePath))
                            {
                                zip.AddFile(filePath, _tracer, String.Empty);
                            }
                        }
                    });
                    return response;
                }
                else
                {
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest,
                        String.Format(CultureInfo.CurrentCulture, Resources.Error_DumpFormatNotSupported, dumpFormat));
                }
            }
        }

        [HttpGet]
        public HttpResponseMessage GCDump(int id, int maxDumpCountK = 0, string format = null)
        {
            using (_tracer.Step("ProcessController.GCDump"))
            {
                DumpFormat dumpFormat = ParseDumpFormat(format, DumpFormat.DiagSession);
                var process = GetProcessById(id);
                var ext = dumpFormat == DumpFormat.DiagSession ? "diagsession" : "gcdump";

                string dumpFile = Path.Combine(_environment.LogFilesPath, "minidump", "dump." + ext);
                FileSystemHelpers.EnsureDirectory(Path.GetDirectoryName(dumpFile));
                FileSystemHelpers.DeleteFileSafe(dumpFile);

                string resourcePath = GetResponseFileName(process.ProcessName, "gcdump");
                try
                {
                    using (_tracer.Step(String.Format("GCDump pid={0}, name={1}, file={2}", process.Id, process.ProcessName, dumpFile)))
                    {
                        process.GCDump(dumpFile, resourcePath, maxDumpCountK, _tracer, _settings.GetCommandIdleTimeout());
                        _tracer.Trace("GCDump size={0}", new FileInfo(dumpFile).Length);
                    }
                }
                catch (Exception ex)
                {
                    _tracer.TraceError(ex);
                    FileSystemHelpers.DeleteFileSafe(dumpFile);
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, ex.Message);
                }

                if (dumpFormat == DumpFormat.Zip)
                {
                    string responseFileName = GetResponseFileName(process.ProcessName, "zip");
                    HttpResponseMessage response = Request.CreateResponse();
                    response.Content = ZipStreamContent.Create(responseFileName, _tracer, zip =>
                    {
                        try
                        {
                            zip.AddFile(dumpFile, _tracer, String.Empty);
                        }
                        finally
                        {
                            FileSystemHelpers.DeleteFileSafe(dumpFile);
                        }
                    });
                    return response;
                }
                else
                {
                    string responseFileName = GetResponseFileName(process.ProcessName, ext);
                    HttpResponseMessage response = Request.CreateResponse();
                    response.Content = new StreamContent(FileStreamWrapper.OpenRead(dumpFile));
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment");
                    response.Content.Headers.ContentDisposition.FileName = responseFileName;
                    return response;
                }
            }
        }

        private static string GetResponseFileName(string prefix, string ext)
        {
            return String.Format(CultureInfo.InvariantCulture, "{0}-{1}-{2:MM-dd-HH-mm-ss}.{3}", prefix, InstanceIdUtility.GetShortInstanceId(), DateTime.UtcNow, ext);
        }

        private DumpFormat ParseDumpFormat(string format, DumpFormat defaultFormat)
        {
            if (String.IsNullOrEmpty(format))
            {
                return defaultFormat;
            }

            try
            {
                return (DumpFormat)Enum.Parse(typeof(DumpFormat), format, ignoreCase: true);
            }
            catch (Exception ex)
            {
                _tracer.TraceError(ex);

                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ex.Message));
            }
        }

        private IEnumerable<string> GetOpenFileHandles(int processId)
        {
            var exe = new Executable(Path.Combine(_environment.ScriptPath, "KuduHandles.exe"), _environment.RootPath,
                _settings.GetCommandIdleTimeout());
            var result = exe.Execute(_tracer, processId.ToString());
            var stdout = result.Item1;
            var stderr = result.Item2;

            if (!String.IsNullOrEmpty(stderr))
                _tracer.TraceError(stderr);

            if (String.IsNullOrEmpty(stdout))
                return Enumerable.Empty<string>();
            return stdout.Split(new[] { System.Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private IEnumerable<ProcessThreadInfo> GetThreads(Process process, string href)
        {
            List<ProcessThreadInfo> threads = new List<ProcessThreadInfo>();
            foreach (ProcessThread thread in process.Threads)
            {
                threads.Add(GetProcessThreadInfo(thread, href + @"/threads/" + thread.Id));
            }

            return threads;
        }

        private ProcessThreadInfo GetProcessThreadInfo(ProcessThread thread, string href, bool details = false)
        {
            var threadInfo = new ProcessThreadInfo
            {
                Id = thread.Id,
                State = thread.ThreadState.ToString(),
                Href = new Uri(href)
            };

            if (details)
            {
                threadInfo.Process = new Uri(href.Substring(0, href.IndexOf(@"/threads/", StringComparison.OrdinalIgnoreCase)));
                threadInfo.BasePriority = SafeGetValue(() => thread.BasePriority, -1);
                threadInfo.PriorityLevel = thread.PriorityLevel.ToString();
                threadInfo.CurrentPriority = SafeGetValue(() => thread.CurrentPriority, -1);
                threadInfo.StartTime = SafeGetValue(() => thread.StartTime.ToUniversalTime(), DateTime.MinValue);
                threadInfo.TotalProcessorTime = SafeGetValue(() => thread.TotalProcessorTime, TimeSpan.FromSeconds(-1));
                threadInfo.UserProcessorTime = SafeGetValue(() => thread.UserProcessorTime, TimeSpan.FromSeconds(-1));
                threadInfo.PriviledgedProcessorTime = SafeGetValue(() => thread.PrivilegedProcessorTime, TimeSpan.FromSeconds(-1));
                threadInfo.StartAddress = "0x" + thread.StartAddress.ToInt64().ToString("X");

                if (thread.ThreadState == ThreadState.Wait)
                {
                    threadInfo.WaitReason = thread.WaitReason.ToString();
                }
                else
                {
                    threadInfo.WaitReason = "Cannot obtain wait reason unless thread is in waiting state";
                }
            }

            return threadInfo;
        }

        [SuppressMessage("Microsoft.Maintainability", "CA1502:AvoidExcessiveComplexity", Justification = "")]
        private ProcessInfo GetProcessInfo(Process process, string href, bool details = false)
        {
            href = href.TrimEnd('/');
            if (href.EndsWith("/0", StringComparison.OrdinalIgnoreCase))
            {
                href = href.Substring(0, href.Length - 1) + process.Id;
            }

            var selfLink = new Uri(href);
            var info = new ProcessInfo
            {
                Id = process.Id,
                Name = process.ProcessName,
                Href = selfLink
            };

            if (details)
            {
                // this could fail access denied
                info.HandleCount = SafeGetValue(() => process.HandleCount, -1);
                info.ThreadCount = SafeGetValue(() => process.Threads.Count, -1);
                info.ModuleCount = SafeGetValue(() => process.Modules.Count, -1);
                info.FileName = SafeGetValue(() => process.MainModule.FileName, "N/A");

                // always return empty
                //info.Arguments = SafeGetValue(() => process.StartInfo.Arguments, "N/A");
                //info.UserName = SafeGetValue(() => process.StartInfo.UserName, "N/A");

                info.StartTime = SafeGetValue(() => process.StartTime.ToUniversalTime(), DateTime.MinValue);
                info.TotalProcessorTime = SafeGetValue(() => process.TotalProcessorTime, TimeSpan.FromSeconds(-1));
                info.UserProcessorTime = SafeGetValue(() => process.UserProcessorTime, TimeSpan.FromSeconds(-1));
                info.PrivilegedProcessorTime = SafeGetValue(() => process.PrivilegedProcessorTime, TimeSpan.FromSeconds(-1));

                info.PagedSystemMemorySize64 = SafeGetValue(() => process.PagedSystemMemorySize64, -1);
                info.NonpagedSystemMemorySize64 = SafeGetValue(() => process.NonpagedSystemMemorySize64, -1);
                info.PagedMemorySize64 = SafeGetValue(() => process.PagedMemorySize64, -1);
                info.PeakPagedMemorySize64 = SafeGetValue(() => process.PeakPagedMemorySize64, -1);
                info.WorkingSet64 = SafeGetValue(() => process.WorkingSet64, -1);
                info.PeakWorkingSet64 = SafeGetValue(() => process.PeakWorkingSet64, -1);
                info.VirtualMemorySize64 = SafeGetValue(() => process.VirtualMemorySize64, -1);
                info.PeakVirtualMemorySize64 = SafeGetValue(() => process.PeakVirtualMemorySize64, -1);
                info.PrivateMemorySize64 = SafeGetValue(() => process.PrivateMemorySize64, -1);

                info.MiniDump = new Uri(selfLink + "/dump");
                if (ProcessExtensions.SupportGCDump)
                {
                    info.GCDump = new Uri(selfLink + "/gcdump");
                }
                info.OpenFileHandles = SafeGetValue(() => GetOpenFileHandles(process.Id), Enumerable.Empty<string>());
                info.Parent = new Uri(selfLink, SafeGetValue(() => process.GetParentId(_tracer), 0).ToString());
                info.Children = SafeGetValue(() => process.GetChildren(_tracer, recursive: false), Enumerable.Empty<Process>()).Select(c => new Uri(selfLink, c.Id.ToString()));
                info.Threads = SafeGetValue(() => GetThreads(process, selfLink.ToString()), Enumerable.Empty<ProcessThreadInfo>());
            }

            return info;
        }

        private Process GetProcessById(int id)
        {
            try
            {
                return id <= 0 ? Process.GetCurrentProcess() : Process.GetProcessById(id);
            }
            catch (ArgumentException ex)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.NotFound, ex.Message));
            }
        }

        private T SafeGetValue<T>(Func<T> func, T defaultValue)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                _tracer.TraceError(ex);
            }

            return defaultValue;
        }

        public enum DumpFormat
        {
            Raw,
            Zip,
            DiagSession,
        }

        public class FileStreamWrapper : DelegatingStream
        {
            private readonly string _path;

            private FileStreamWrapper(string path)
                : base(FileSystemHelpers.Instance.File.OpenRead(path))
            {
                _path = path;
            }

            protected override void Dispose(bool disposing)
            {
                try
                {
                    base.Dispose(disposing);
                }
                finally
                {
                    FileSystemHelpers.DeleteFileSafe(_path);
                }
            }

            public static Stream OpenRead(string path)
            {
                return new FileStreamWrapper(path);
            }
        }
    }
}