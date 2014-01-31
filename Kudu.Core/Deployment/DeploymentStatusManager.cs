using System;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using Kudu.Contracts.Infrastructure;
using Kudu.Core.Infrastructure;

namespace Kudu.Core.Deployment
{
    public class DeploymentStatusManager : IDeploymentStatusManager
    {
        public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(60);
        private static IFileSystem FileSystem { get { return FileSystemHelpers.Instance; } }
        private readonly IEnvironment _environment;
        private readonly IOperationLock _statusLock;
        private readonly string _activeFile;

        public DeploymentStatusManager(IEnvironment environment,
                                       IOperationLock statusLock)
        {
            _environment = environment;
            _statusLock = statusLock;
            _activeFile = Path.Combine(environment.DeploymentsPath, Constants.ActiveDeploymentFile);
        }

        public IDeploymentStatusFile Create(string id)
        {
            return DeploymentStatusFile.Create(id, _environment, _statusLock);
        }

        public IDeploymentStatusFile Open(string id)
        {
            return DeploymentStatusFile.Open(id, _environment, _statusLock);
        }

        public void Delete(string id)
        {
            string path = Path.Combine(_environment.DeploymentsPath, id);

            _statusLock.LockOperation(() =>
            {
                FileSystemHelpers.DeleteDirectorySafe(path, ignoreErrors: true);

                // Used for ETAG
                if (FileSystem.File.Exists(_activeFile))
                {
                    FileSystem.File.SetLastWriteTimeUtc(_activeFile, DateTime.UtcNow);
                }
                else
                {
                    FileSystem.File.WriteAllText(_activeFile, String.Empty);
                }
            }, LockTimeout);
        }

        public IOperationLock Lock
        {
            get { return _statusLock; }
        }

        public string ActiveDeploymentId
        {
            get
            {
                return _statusLock.LockOperation(() =>
                {
                    if (FileSystem.File.Exists(_activeFile))
                    {
                        return FileSystem.File.ReadAllText(_activeFile);
                    }

                    return null;
                }, LockTimeout);
            }
            set
            {
                _statusLock.LockOperation(() => FileSystem.File.WriteAllText(_activeFile, value), LockTimeout);
            }
        }


        public DateTime LastModifiedTime
        {
            get
            {
                return _statusLock.LockOperation(() =>
                {
                    if (FileSystem.File.Exists(_activeFile))
                    {
                        return FileSystem.File.GetLastWriteTimeUtc(_activeFile);
                    }
                    else
                    {
                        return DateTime.MinValue;
                    }
                }, LockTimeout);
            }
        }
    }
}
