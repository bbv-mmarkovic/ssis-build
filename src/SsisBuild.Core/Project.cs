﻿//-----------------------------------------------------------------------
//   Copyright 2017 Roman Tumaykin
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.IO.Packaging;
using System.Xml;
using SsisBuild.Core.Helpers;

namespace SsisBuild.Core
{
    public sealed class Project : IProject
    {
        public ProtectionLevel ProtectionLevel => ProjectManifest?.ProtectonLevel ?? ProtectionLevel.DontSaveSensitive;

        public string VersionMajor
        {
            get { return ProjectManifest?.VersionMajor; }
            set
            {
                if (ProjectManifest != null)
                    ProjectManifest.VersionMajor = value;
            }
        }

        public string VersionMinor
        {
            get { return ProjectManifest?.VersionMinor; }
            set
            {
                if (ProjectManifest != null)
                    ProjectManifest.VersionMinor = value;
            }
        }

        public string VersionBuild
        {
            get { return ProjectManifest?.VersionBuild; }
            set
            {
                if (ProjectManifest != null)
                    ProjectManifest.VersionBuild = value;
            }
        }

        public string VersionComments
        {
            get { return ProjectManifest?.VersionComments; }
            set
            {
                if (ProjectManifest != null)
                    ProjectManifest.VersionComments = value;
            }
        }

        public string Description
        {
            get { return ProjectManifest?.Description; }
            set
            {
                if (ProjectManifest != null)
                    ProjectManifest.Description = value;
            }
        }
        private ProjectManifest ProjectManifest => (_projectManifest as ProjectManifest);

        private readonly IDictionary<string, Parameter> _parameters;
        public IReadOnlyDictionary<string, Parameter> Parameters { get; }

        private ProjectFile _projectManifest;
        private ProjectFile _projectParams;
        private readonly IDictionary<string, ProjectFile> _projectConnections;
        private readonly IDictionary<string, ProjectFile> _packages;

        private bool _isLoaded;

        public Project()
        {
            _parameters = new Dictionary<string, Parameter>();

            Parameters = new ReadOnlyDictionary<string, Parameter>(_parameters);

            _packages = new Dictionary<string, ProjectFile>();
            _projectConnections = new Dictionary<string, ProjectFile>();

            _isLoaded = false;
        }

        public void UpdateParameter(string parameterName, string value, ParameterSource source)
        {
            if (!_isLoaded)
                throw new ProjectNotLoadedException();

            if (_parameters.ContainsKey(parameterName))
                _parameters[parameterName].SetValue(value, source);
        }

        private void LoadParameters()
        {
            foreach (var projectParametersParameter in _projectParams.Parameters)
            {
                _parameters.Add(projectParametersParameter.Key, projectParametersParameter.Value);
            }

            foreach (var projectManifestParameter in _projectManifest.Parameters)
            {
                _parameters.Add(projectManifestParameter.Key, projectManifestParameter.Value);
            }

        }

        public void Save(string destinationFilePath, ProtectionLevel protectionLevel, string password)
        {
            if (!_isLoaded)
                throw new ProjectNotLoadedException();

            if (Path.GetExtension(destinationFilePath) != ".ispac")
                throw new Exception($"Destination file name must have an ispac extension. Currently: {destinationFilePath}");

            if (File.Exists(destinationFilePath))
                File.Delete(destinationFilePath);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath));

            using (var ispacStream = new FileStream(destinationFilePath, FileMode.Create))
            {
                Save(ispacStream, protectionLevel, password);
            }

        }

        public void Save(string destinationFilePath) => Save(destinationFilePath, ProtectionLevel.DontSaveSensitive, null);

        public void Save(Stream destinationStream, ProtectionLevel protectionLevel, string password)
        {
            if (!_isLoaded)
                throw new ProjectNotLoadedException();

            using (var ispacArchive = new ZipArchive(destinationStream, ZipArchiveMode.Create))
            {
                var manifest = ispacArchive.CreateEntry("@Project.manifest");
                using (var stream = manifest.Open())
                {
                    _projectManifest.Save(stream, protectionLevel, password);
                }

                var projectParams = ispacArchive.CreateEntry("Project.params");
                using (var stream = projectParams.Open())
                {
                    _projectParams.Save(stream, protectionLevel, password);
                }

                var contentTypes = ispacArchive.CreateEntry("[Content_Types].xml");
                using (var stream = contentTypes.Open())
                {
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(
                            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"dtsx\" ContentType=\"text/xml\" /><Default Extension=\"conmgr\" ContentType=\"text/xml\" /><Default Extension=\"params\" ContentType=\"text/xml\" /><Default Extension=\"manifest\" ContentType=\"text/xml\" /></Types>");
                    }
                }

                foreach (var package in _packages)
                {
                    var name = PackUriHelper.CreatePartUri(new Uri(package.Key, UriKind.Relative)).OriginalString.Substring(1);
                    var packageEntry = ispacArchive.CreateEntry(name);
                    using (var stream = packageEntry.Open())
                    {
                        package.Value.Save(stream, protectionLevel, password);
                    }
                }

                foreach (var projectConnection in _projectConnections)
                {
                    var name = PackUriHelper.CreatePartUri(new Uri(projectConnection.Key, UriKind.Relative)).OriginalString.Substring(1);
                    var projectConnectionEntry = ispacArchive.CreateEntry(name);
                    using (var stream = projectConnectionEntry.Open())
                    {
                        projectConnection.Value.Save(stream, protectionLevel, password);
                    }
                }
            }
        }
        public void LoadFromIspac(string filePath, string password)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File {filePath} does not exist or you don't have permissions to access it.", filePath);

            if (!filePath.EndsWith(".ispac", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"File {filePath} does not have an .ispac extension.");

            using (var ispacStream = new FileStream(filePath, FileMode.Open))
            {
                using (var ispacArchive = new ZipArchive(ispacStream, ZipArchiveMode.Read))
                {
                    foreach (var ispacArchiveEntry in ispacArchive.Entries)
                    {
                        var fileName = ispacArchiveEntry.FullName;
                        using (var fileStream = ispacArchiveEntry.Open())
                        {
                            switch (Path.GetExtension(fileName))
                            {
                                case ".manifest":
                                    _projectManifest = new ProjectManifest().Initialize(fileStream, password);
                                    break;

                                case ".params":
                                    _projectParams = new ProjectParams().Initialize(fileStream, password);
                                    break;

                                case ".dtsx":
                                    _packages.Add(fileName, new Package().Initialize(fileStream, password));
                                    break;

                                case ".conmgr":
                                    _projectConnections.Add(fileName, new ProjectConnection().Initialize(fileStream, password));
                                    break;

                                case ".xml":
                                    break;

                                default:
                                    throw new Exception($"Unexpected file {fileName} in {filePath}.");
                            }
                        }
                    }
                }
            }

            LoadParameters();

            _isLoaded = true;
        }

        public void LoadFromDtproj(string filePath, string configurationName, string password)
        {
            if (!File.Exists(filePath))
                throw new Exception($"File {filePath} does not exist.");

            if (!filePath.EndsWith(".dtproj", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"File {filePath} does not have an .dtproj extension.");

            var dtprojXmlDoc = new XmlDocument();
            dtprojXmlDoc.Load(filePath);
            ValidateDeploymentMode(dtprojXmlDoc);

            var nsManager = dtprojXmlDoc.GetNameSpaceManager();

            var projectXmlNode = dtprojXmlDoc.SelectSingleNode("/Project/DeploymentModelSpecificContent/Manifest/SSIS:Project", nsManager);

            if (projectXmlNode == null)
                throw new Exception("Project Manifest Node was not found.");

            var projectDirectory = Path.GetDirectoryName(filePath);

            if (string.IsNullOrWhiteSpace(projectDirectory))
                throw new Exception("Failed to retrieve directory of the source project.");

            using (var stream = new MemoryStream())
            {
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(projectXmlNode.OuterXml);
                    writer.Flush();
                    stream.Position = 0;

                    _projectManifest = new ProjectManifest().Initialize(stream, password);
                }
            }

            _projectParams = new ProjectParams().Initialize(Path.Combine(projectDirectory, "Project.params"), password);

            foreach (var connectionManagerName in ProjectManifest.ConnectionManagerNames)
            {
                _projectConnections.Add(connectionManagerName, new ProjectConnection().Initialize(Path.Combine(projectDirectory, connectionManagerName), password));
            }

            foreach (var packageName in ProjectManifest.PackageNames)
            {
                _packages.Add(packageName, new Package().Initialize(Path.Combine(projectDirectory, packageName), password));
            }

            LoadParameters();

            foreach (var configurationParameter in new Configuration(configurationName).Initialize(filePath, password).Parameters)
            {
                UpdateParameter(configurationParameter.Key, configurationParameter.Value.Value, ParameterSource.Configuration);
            }

            var userConfigurationFilePath = $"{filePath}.user";
            if (File.Exists(userConfigurationFilePath))
            {
                foreach (var userConfigurationParameter in new UserConfiguration(configurationName).Initialize(userConfigurationFilePath, password).Parameters)
                {
                    UpdateParameter(userConfigurationParameter.Key, null, ParameterSource.Configuration);
                }
            }

            _isLoaded = true;
        }

        private static void ValidateDeploymentMode(XmlNode dtprojXmlDoc)
        {
            var deploymentModel = dtprojXmlDoc.SelectSingleNode("/Project/DeploymentModel")?.InnerText;

            if (deploymentModel != "Project")
                throw new Exception("This build method only apply to Project deployment model.");
        }
    }
}
