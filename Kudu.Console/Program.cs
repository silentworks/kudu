﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using Kudu.Console.Services;
using Kudu.Contracts.Infrastructure;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Deployment;
using Kudu.Core.Deployment.Generator;
using Kudu.Core.Hooks;
using Kudu.Core.Infrastructure;
using Kudu.Core.Settings;
using Kudu.Core.SourceControl;
using Kudu.Core.SourceControl.Git;
using Kudu.Core.Tracing;
using Kudu.Services;
using XmlSettings;

namespace Kudu.Console
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            // Turn flag on in app.config to wait for debugger on launch
            if (ConfigurationManager.AppSettings["WaitForDebuggerOnStart"] == "true")
            {
                while (!Debugger.IsAttached)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            if (args.Length < 2)
            {
                System.Console.WriteLine("Usage: kudu.exe appRoot wapTargets [deployer]");
                return 1;
            }

            // The post receive hook launches the exe from sh and intereprets newline differently.
            // This fixes very wacky issues with how the output shows up in the conosle on push
            System.Console.Error.NewLine = "\n";
            System.Console.Out.NewLine = "\n";

            System.Environment.SetEnvironmentVariable("GIT_DIR", null, System.EnvironmentVariableTarget.Process);

            string appRoot = args[0];
            string wapTargets = args[1];
            string deployer = args.Length == 2 ? null : args[2];

            IEnvironment env = GetEnvironment(appRoot);
            ISettings settings = new XmlSettings.Settings(GetSettingsPath(env));
            IDeploymentSettingsManager settingsManager = new DeploymentSettingsManager(settings);

            // Adjust repo path
            env.RepositoryPath = Path.Combine(env.SiteRootPath, settingsManager.GetRepositoryPath());

            // Setup the trace
            TraceLevel level = settingsManager.GetTraceLevel();
            ITracer tracer = GetTracer(env, level);
            ITraceFactory traceFactory = new TracerFactory(() => tracer);

            // Calculate the lock path
            string lockPath = Path.Combine(env.SiteRootPath, Constants.LockPath);
            string deploymentLockPath = Path.Combine(lockPath, Constants.DeploymentLockFile);
            string statusLockPath = Path.Combine(lockPath, Constants.StatusLockFile);
            string hooksLockPath = Path.Combine(lockPath, Constants.HooksLockFile);
            string analyticsPath = env.AnalyticsPath;

            IOperationLock deploymentLock = new LockFile(deploymentLockPath, traceFactory);
            IOperationLock statusLock = new LockFile(statusLockPath, traceFactory);
            IOperationLock hooksLock = new LockFile(hooksLockPath, traceFactory);

            IBuildPropertyProvider buildPropertyProvider = new BuildPropertyProvider();
            ISiteBuilderFactory builderFactory = new SiteBuilderFactory(buildPropertyProvider, env);

            IRepository gitRepository = new GitExeRepository(env, settingsManager, traceFactory);

            IAnalytics analytics = new Analytics(settingsManager, tracer, analyticsPath);

            IWebHooksManager hooksManager = new WebHooksManager(tracer, env, hooksLock);
            var logger = new ConsoleLogger();
            IDeploymentManager deploymentManager = new DeploymentManager(builderFactory,
                                                          env,
                                                          traceFactory,
                                                          analytics,
                                                          settingsManager,
                                                          new DeploymentStatusManager(env, statusLock),
                                                          deploymentLock,
                                                          GetLogger(env, level, logger),
                                                          hooksManager);

            var step = tracer.Step("Executing external process", new Dictionary<string, string>
            {
                { "type", "process" },
                { "path", "kudu.exe" },
                { "arguments", appRoot + " " + wapTargets }
            });

            using (step)
            {
                try
                {
                    deploymentManager.DeployAsync(gitRepository, changeSet: null, deployer: deployer, clean: false)
                        .Wait();
                }
                catch (Exception e)
                {
                    System.Console.Error.WriteLine(e.Message);
                    System.Console.Error.WriteLine(Resources.Log_DeploymentError);
                    return 1;
                }
            }

            if (logger.HasErrors)
            {
                System.Console.Error.WriteLine(Resources.Log_DeploymentError);
                return 1;
            }

            return 0;
        }

        private static ITracer GetTracer(IEnvironment env, TraceLevel level)
        {
            if (level > TraceLevel.Off)
            {
                string traceLockPath = Path.Combine(env.TracePath, Constants.TraceLockFile);
                var traceLock = new LockFile(traceLockPath, NullTracerFactory.Instance);
                var tracer = new Tracer(Path.Combine(env.TracePath, Constants.TraceFile), level, traceLock);
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    // Kudu.exe is executed as part of git.exe (post-receive), giving its initial depth of 4 indentations
                    string logPath = Path.Combine(env.TracePath, logFile);
                    return new CascadeTracer(tracer, new TextTracer(logPath, level, 4));
                }

                return tracer;
            }

            return NullTracer.Instance;
        }

        private static ILogger GetLogger(IEnvironment env, TraceLevel level, ILogger primary)
        {
            if (level > TraceLevel.Off)
            {
                string logFile = System.Environment.GetEnvironmentVariable(Constants.TraceFileEnvKey);
                if (!String.IsNullOrEmpty(logFile))
                {
                    string logPath = Path.Combine(env.RootPath, Constants.DeploymentTracePath, logFile);
                    return new CascadeLogger(primary, new TextLogger(logPath));
                }
            }

            return primary;
        }

        private static string GetSettingsPath(IEnvironment environment)
        {
            return Path.Combine(environment.DeploymentsPath, Constants.DeploySettingsPath);
        }

        private static IEnvironment GetEnvironment(string siteRoot)
        {
            string root = Path.GetFullPath(Path.Combine(siteRoot, ".."));

            // REVIEW: this looks wrong because it ignores SCM_REPOSITORY_PATH
            string repositoryPath = Path.Combine(siteRoot, Constants.RepositoryPath);

            string binPath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

            return new Kudu.Core.Environment(root, 
                binPath, 
                repositoryPath);
        }
    }
}