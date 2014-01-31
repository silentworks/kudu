﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Routing;
using Kudu.Contracts.Editor;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Infrastructure;

namespace Kudu.Services.Infrastructure
{
    /// <summary>
    /// Provides common functionality for Virtual File System controllers.
    /// </summary>
    public abstract class VfsControllerBase : ApiController
    {
        private const string DirectoryEnumerationSearchPattern = "*";
        public const char UriSegmentSeparator = '/';

        private static readonly char[] _uriSegmentSeparator = new char[] { UriSegmentSeparator };
        private static readonly MediaTypeHeaderValue _directoryMediaType = MediaTypeHeaderValue.Parse("inode/directory");

        protected const int BufferSize = 32 * 1024;

        protected VfsControllerBase(ITracer tracer, IEnvironment environment, string rootPath)
        {
            if (rootPath == null)
            {
                throw new ArgumentNullException("rootPath");
            }
            Tracer = tracer;
            Environment = environment;
            RootPath = Path.GetFullPath(rootPath.TrimEnd(Path.DirectorySeparatorChar));
            MediaTypeMap = MediaTypeMap.Default;
        }

        [AcceptVerbs("GET", "HEAD")]
        public virtual Task<HttpResponseMessage> GetItem()
        {
            string localFilePath = GetLocalFilePath();

            HttpResponseMessage response;
            if (VfsSpecialFolders.TryHandleRequest(Request, localFilePath, out response))
            {
                return Task.FromResult(response);
            }

            DirectoryInfoBase info = FileSystem.DirectoryInfo.FromDirectoryName(localFilePath);

            if (info.Attributes < 0)
            {
                HttpResponseMessage notFoundResponse = Request.CreateErrorResponse(HttpStatusCode.NotFound, String.Format("'{0}' not found.", info.FullName));
                return Task.FromResult(notFoundResponse);
            }
            else if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                // If request URI does NOT end in a "/" then redirect to one that does
                if (localFilePath[localFilePath.Length - 1] != Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path += "/";
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }
                else
                {
                    return CreateDirectoryGetResponse(info, localFilePath);
                }
            }
            else
            {
                // If request URI ends in a "/" then redirect to one that does not
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path = location.Path.TrimEnd(_uriSegmentSeparator);
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }

                // We are ready to get the file
                return CreateItemGetResponse(info, localFilePath);
            }
        }

        [HttpPut]
        public virtual Task<HttpResponseMessage> PutItem()
        {
            string localFilePath = GetLocalFilePath();

            HttpResponseMessage response;
            if (VfsSpecialFolders.TryHandleRequest(Request, localFilePath, out response))
            {
                return Task.FromResult(response);
            }

            DirectoryInfoBase info = FileSystem.DirectoryInfo.FromDirectoryName(localFilePath);
            bool itemExists = info.Attributes >= 0;

            if (itemExists && (info.Attributes & FileAttributes.Directory) != 0)
            {
                return CreateDirectoryPutResponse(info, localFilePath);
            }
            else
            {
                // If request URI ends in a "/" then attempt to create the directory.
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    return CreateDirectoryPutResponse(info, localFilePath);
                }

                // We are ready to update the file
                return CreateItemPutResponse(info, localFilePath, itemExists);
            }
        }

        [HttpDelete]
        public virtual Task<HttpResponseMessage> DeleteItem(bool recursive = false)
        {
            string localFilePath = GetLocalFilePath();

            HttpResponseMessage response;
            if (VfsSpecialFolders.TryHandleRequest(Request, localFilePath, out response))
            {
                return Task.FromResult(response);
            }

            DirectoryInfoBase dirInfo = FileSystem.DirectoryInfo.FromDirectoryName(localFilePath);

            if (dirInfo.Attributes < 0)
            {
                HttpResponseMessage notFoundResponse = Request.CreateErrorResponse(HttpStatusCode.NotFound, String.Format("'{0}' not found.", dirInfo.FullName));
                return Task.FromResult(notFoundResponse);
            }
            else if ((dirInfo.Attributes & FileAttributes.Directory) != 0)
            {
                try
                {
                    dirInfo.Delete(recursive);
                }
                catch (Exception ex)
                {
                    Tracer.TraceError(ex);
                    HttpResponseMessage conflictDirectoryResponse = Request.CreateErrorResponse(
                        HttpStatusCode.Conflict, Resources.VfsControllerBase_CannotDeleteDirectory);
                    return Task.FromResult(conflictDirectoryResponse);
                }

                // Delete directory succeeded.
                HttpResponseMessage successResponse = Request.CreateResponse(HttpStatusCode.OK);
                return Task.FromResult(successResponse);
            }
            else
            {
                // If request URI ends in a "/" then redirect to one that does not
                if (localFilePath[localFilePath.Length - 1] == Path.DirectorySeparatorChar)
                {
                    HttpResponseMessage redirectResponse = Request.CreateResponse(HttpStatusCode.TemporaryRedirect);
                    UriBuilder location = new UriBuilder(Request.RequestUri);
                    location.Path = location.Path.TrimEnd(_uriSegmentSeparator);
                    redirectResponse.Headers.Location = location.Uri;
                    return Task.FromResult(redirectResponse);
                }

                // We are ready to delete the file
                var fileInfo = FileSystem.FileInfo.FromFileName(localFilePath);
                return CreateFileDeleteResponse(fileInfo);
            }
        }

        protected static IFileSystem FileSystem { get { return FileSystemHelpers.Instance; } }

        protected ITracer Tracer { get; private set; }

        protected IEnvironment Environment { get; private set; }

        protected string RootPath { get; private set; }

        protected MediaTypeMap MediaTypeMap { get; private set; }

        protected virtual Task<HttpResponseMessage> CreateDirectoryGetResponse(DirectoryInfoBase info, string localFilePath)
        {
            Contract.Assert(info != null);
            try
            {
                // Enumerate directory
                IEnumerable<VfsStatEntry> directory = GetDirectoryResponse(info.GetFileSystemInfos());
                HttpResponseMessage successDirectoryResponse = Request.CreateResponse<IEnumerable<VfsStatEntry>>(HttpStatusCode.OK, directory);
                return Task.FromResult(successDirectoryResponse);
            }
            catch (Exception e)
            {
                Tracer.TraceError(e);
                HttpResponseMessage errorResponse = Request.CreateErrorResponse(HttpStatusCode.InternalServerError, e.Message);
                return Task.FromResult(errorResponse);
            }
        }

        protected abstract Task<HttpResponseMessage> CreateItemGetResponse(FileSystemInfoBase info, string localFilePath);

        protected virtual Task<HttpResponseMessage> CreateDirectoryPutResponse(DirectoryInfoBase info, string localFilePath)
        {
            HttpResponseMessage conflictDirectoryResponse = Request.CreateErrorResponse(
                HttpStatusCode.Conflict, Resources.VfsController_CannotUpdateDirectory);
            return Task.FromResult(conflictDirectoryResponse);
        }

        protected abstract Task<HttpResponseMessage> CreateItemPutResponse(FileSystemInfoBase info, string localFilePath, bool itemExists);

        protected virtual Task<HttpResponseMessage> CreateFileDeleteResponse(FileInfoBase info)
        {
            // Generate file response
            try
            {
                using (Stream fileStream = GetFileDeleteStream(info))
                {
                    info.Delete();
                }
                HttpResponseMessage successResponse = Request.CreateResponse(HttpStatusCode.OK);
                return Task.FromResult(successResponse);
            }
            catch (Exception e)
            {
                // Could not delete the file
                Tracer.TraceError(e);
                HttpResponseMessage notFoundResponse = Request.CreateErrorResponse(HttpStatusCode.NotFound, e);
                return Task.FromResult(notFoundResponse);
            }
        }

        /// <summary>
        /// Indicates whether this is a conditional range request containing an
        /// If-Range header with a matching etag and a Range header indicating the 
        /// desired ranges
        /// </summary>
        protected bool IsRangeRequest(EntityTagHeaderValue currentEtag)
        {
            if (currentEtag != null && Request.Headers.IfRange != null && Request.Headers.Range != null)
            {
                // First check that the etag matches so that we can consider the range request
                if (currentEtag.Equals(Request.Headers.IfRange.EntityTag))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Indicates whether this is a If-None-Match request with a matching etag.
        /// </summary>
        protected bool IsIfNoneMatchRequest(EntityTagHeaderValue currentEtag)
        {
            return currentEtag != null && Request.Headers.IfNoneMatch != null &&
                Request.Headers.IfNoneMatch.Any(entityTag => currentEtag.Equals(entityTag));
        }

        /// <summary>
        /// Provides a common way for opening a file stream for shared reading from a file.
        /// </summary>
        protected static Stream GetFileReadStream(string localFilePath)
        {
            Contract.Assert(localFilePath != null);

            // Open file exclusively for read-sharing
            return new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true);
        }

        /// <summary>
        /// Provides a common way for opening a file stream for writing exclusively to a file. 
        /// </summary>
        protected static Stream GetFileWriteStream(string localFilePath, bool fileExists)
        {
            Contract.Assert(localFilePath != null);

            // Create path if item doesn't already exist
            if (!fileExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(localFilePath));
            }

            // Open file exclusively for write without any sharing
            return new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        }

        /// <summary>
        /// Provides a common way for opening a file stream for exclusively deleting the file. 
        /// </summary>
        private static Stream GetFileDeleteStream(FileInfoBase file)
        {
            Contract.Assert(file != null);

            // Open file exclusively for delete sharing only
            return file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        // internal for testing purpose
        internal string GetLocalFilePath()
        {
            IHttpRouteData routeData = Request.GetRouteData();

            string result;
            if (VfsSpecialFolders.TryParse(routeData, out result))
            {
                return result;
            }

            result = RootPath;
            if (routeData != null)
            {
                string path = routeData.Values["path"] as string;
                if (!String.IsNullOrEmpty(path))
                {
                    result = FileSystem.Path.GetFullPath(Path.Combine(result, path));
                }
                else
                {
                    string reqUri = Request.RequestUri.AbsoluteUri;
                    if (reqUri[reqUri.Length - 1] == UriSegmentSeparator)
                    {
                        result = Path.GetFullPath(result + Path.DirectorySeparatorChar);
                    }
                }
            }
            return result;
        }

        private IEnumerable<VfsStatEntry> GetDirectoryResponse(FileSystemInfoBase[] infos)
        {
            string baseAddress = Request.RequestUri.AbsoluteUri;
            foreach (FileSystemInfoBase fileSysInfo in infos)
            {
                bool isDirectory = (fileSysInfo.Attributes & FileAttributes.Directory) != 0;
                string mime = isDirectory ? _directoryMediaType.ToString() : MediaTypeMap.GetMediaType(fileSysInfo.Extension).ToString();
                string unescapedHref = isDirectory ? fileSysInfo.Name + UriSegmentSeparator : fileSysInfo.Name;
                long size = isDirectory ? 0 : ((FileInfoBase)fileSysInfo).Length;

                yield return new VfsStatEntry
                {
                    Name = fileSysInfo.Name,
                    MTime = fileSysInfo.LastWriteTimeUtc,
                    Mime = mime,
                    Size = size,
                    Href = baseAddress + Uri.EscapeUriString(unescapedHref),
                    Path = fileSysInfo.FullName
                };
            }

            // add special folders when requesting Root url
            IHttpRouteData routeData = Request.GetRouteData();
            if (routeData != null && String.IsNullOrEmpty(routeData.Values["path"] as string))
            {
                foreach (var entry in VfsSpecialFolders.GetEntries(baseAddress))
                {
                    yield return entry;
                }
            }
        }
    }
}
