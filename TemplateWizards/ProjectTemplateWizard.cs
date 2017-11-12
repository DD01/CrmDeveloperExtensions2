﻿using CrmDeveloperExtensions2.Core;
using CrmDeveloperExtensions2.Core.Vs;
using EnvDTE;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TemplateWizard;
using NuGet.VisualStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Xml;

namespace TemplateWizards
{
    public class ProjectTemplateWizard : IWizard
    {
        private DTE _dte;
        private string _coreVersion;
        private string _clientVersion;
        private string _clientPackage;
        private string _crmProjectType = "Plug-in";
        private bool _needsCore;
        private bool _needsWorkflow;
        private bool _needsClient;
        private bool _isUnitTest;
        private bool _signAssembly;
        private string _destDirectory;
        private string _unitTestFrameworkPackage;

        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary, WizardRunKind runKind, object[] customParams)
        {
            try
            {
                _dte = (DTE)automationObject;

                ProjectDataHandler.AddOrUpdateReplacements("$referenceproject$", "False", ref replacementsDictionary);
                if (replacementsDictionary.ContainsKey("$destinationdirectory$"))
                    _destDirectory = replacementsDictionary["$destinationdirectory$"];

                if (replacementsDictionary.ContainsKey("$wizarddata$"))
                {
                    string wizardData = replacementsDictionary["$wizarddata$"];
                    ReadWizardData(wizardData);
                }

                if (_isUnitTest)
                    PreHandleUnitTestProjects(replacementsDictionary);

                if (_needsCore)
                    PreHandleCrmAssemblyProjects(replacementsDictionary);
            }
            catch (WizardBackoutException)
            {
                try
                {
                    DirectoryInfo destination = new DirectoryInfo(replacementsDictionary["$destinationdirectory$"]);
                    Directory.Delete(replacementsDictionary["$destinationdirectory$"]);
                    //Delete solution directory if empty
                    if (destination.Parent != null && FileSystem.IsDirectoryEmpty(replacementsDictionary["$solutiondirectory$"]))
                        Directory.Delete(replacementsDictionary["$solutiondirectory$"]);
                }
                catch
                {
                    // If it fails (doesn't exist/contains files/read-only), let the directory stay.
                }
                throw;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error occurred running wizard:\n\n{ex}");
                throw new WizardCancelledException("Internal error", ex);
            }
        }

        private void PreHandleCrmAssemblyProjects(Dictionary<string, string> replacementsDictionary)
        {
            var sdkVersionPicker = new SdkVersionPicker(_needsWorkflow, _needsClient);
            bool? result = sdkVersionPicker.ShowModal();
            if (!result.HasValue || result.Value == false)
                throw new WizardBackoutException();

            _coreVersion = sdkVersionPicker.CoreVersion;
            _clientVersion = sdkVersionPicker.ClientVersion;
            _clientPackage = sdkVersionPicker.ClientPackage;

            if (!string.IsNullOrEmpty(_clientVersion))
            {
                ProjectDataHandler.AddOrUpdateReplacements("$useXrmToolingClientUsing$",
                    Versioning.StringToVersion(_clientVersion).Major >= 8 ? "1" : "0", ref replacementsDictionary);
            }
        }

        private void PreHandleUnitTestProjects(Dictionary<string, string> replacementsDictionary)
        {
            var testProjectPicker = new TestProjectPicker();
            bool? result = testProjectPicker.ShowModal();
            if (!result.HasValue || result.Value == false)
                throw new WizardBackoutException();

            if (testProjectPicker.SelectedProject != null)
            {
                Solution solution = _dte.Solution;
                Project project = testProjectPicker.SelectedProject;
                string path = string.Empty;
                string projectPath = Path.GetDirectoryName(project.FullName);
                string solutionPath = Path.GetDirectoryName(solution.FullName);
                if (!string.IsNullOrEmpty(projectPath) && !string.IsNullOrEmpty(solutionPath))
                {
                    if (projectPath.StartsWith(solutionPath))
                        path = "..\\" + project.UniqueName;
                    else
                        path = project.FullName;
                }

                ProjectDataHandler.AddOrUpdateReplacements("$referenceproject$", "True", ref replacementsDictionary);
                ProjectDataHandler.AddOrUpdateReplacements("$projectPath$", path, ref replacementsDictionary);
                ProjectDataHandler.AddOrUpdateReplacements("$projectId$", project.Kind, ref replacementsDictionary);
                ProjectDataHandler.AddOrUpdateReplacements("$projectName$", project.Name, ref replacementsDictionary);
            }

            if (testProjectPicker.SelectedUnitTestFramework != null)
            {
                _unitTestFrameworkPackage = testProjectPicker.SelectedUnitTestFramework.NugetName;

                ProjectDataHandler.AddOrUpdateReplacements("$useXrmToolingClientUsing$",
                    testProjectPicker.SelectedUnitTestFramework.CrmMajorVersion >= 8 ? "1" : "0", ref replacementsDictionary);
            }
            else
            {
                if (testProjectPicker.SelectedProject == null)
                    return;

                string version = ProjectWorker.GetSdkCoreVersion(testProjectPicker.SelectedProject);
                ProjectDataHandler.AddOrUpdateReplacements("$useXrmToolingClientUsing$",
                    Versioning.StringToVersion(version).Major >= 8 ? "1" : "0", ref replacementsDictionary);
            }
        }

        public bool ShouldAddProjectItem(string filePath)
        {
            return true;
        }

        public void RunFinished()
        {
        }

        public void BeforeOpeningFile(ProjectItem projectItem)
        {
        }

        public void ProjectItemFinishedGenerating(ProjectItem projectItem)
        {
        }

        public void ProjectFinishedGenerating(Project project)
        {
            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            if (componentModel == null) return;

            var installer = componentModel.GetService<IVsPackageInstaller>();

            switch (_crmProjectType)
            {
                case "Unit":
                    PostHandleUnitTestProjects(project, installer);
                    break;
                case "Console":
                case "Plug-in":
                case "Workflow":
                    PostHandleCrmAssemblyProjects(project, installer);
                    break;
                case "WebResource":
                    PostHandleWebResourceProjects(project);
                    break;
                //case "TypeScript":
                //    HandleTypeScriptProject(project, installer);
                //    break;
                case "SolutionPackage":
                    PostHandleSolutionPackagerProject(project);
                    break;
            }

            if (_isUnitTest)
                PostHandleUnitTestProjects(project, installer);
        }

        private void PostHandleSolutionPackagerProject(Project project)
        {
            foreach (SolutionConfiguration solutionConfiguration in _dte.Solution.SolutionBuild.SolutionConfigurations)
                foreach (SolutionContext solutionContext in solutionConfiguration.SolutionContexts)
                    solutionContext.ShouldBuild = false;

            //Delete bin & obj folders
            Directory.Delete(Path.GetDirectoryName(project.FullName) + "//bin", true);
            Directory.Delete(Path.GetDirectoryName(project.FullName) + "//obj", true);

            //Add the default "_Solutions" folder
            //TODO: make this a user setting
            project.ProjectItems.AddFolder("_Solutions");
        }

        private void PostHandleWebResourceProjects(Project project)
        {
            //Turn off Build option in build configurations
            SolutionConfigurations solutionConfigurations = _dte.Solution.SolutionBuild.SolutionConfigurations;
            string folderProjectFileName = ProjectWorker.GetFolderProjectFileName(project.FullName);
            SolutionWorker.SetBuildConfigurationOff(solutionConfigurations, folderProjectFileName);
        }

        private void PostHandleUnitTestProjects(Project project, IVsPackageInstaller installer)
        {
            NuGetProcessor.InstallPackage(_dte, installer, project, "MSTest.TestAdapter", "1.1.18");
            NuGetProcessor.InstallPackage(_dte, installer, project, "MSTest.TestFramework", "1.1.18");

            if (_unitTestFrameworkPackage != null)
                NuGetProcessor.InstallPackage(_dte, installer, project, _unitTestFrameworkPackage, null);
        }

        private void PostHandleCrmAssemblyProjects(Project project, IVsPackageInstaller installer)
        {
            try
            {
                project.DTE.SuppressUI = true;

                //Pre-2015 use .NET 4.0
                if (Versioning.StringToVersion(_coreVersion).Major < 7)
                    project.Properties.Item("TargetFrameworkMoniker").Value = ".NETFramework,Version=v4.0";

                //Install all the NuGet packages
                project = (Project)((Array)_dte.ActiveSolutionProjects).GetValue(0);
                NuGetProcessor.InstallPackage(_dte, installer, project, Resources.Resource.SdkAssemblyCore, _coreVersion);
                if (_needsWorkflow)
                    NuGetProcessor.InstallPackage(_dte, installer, project, Resources.Resource.SdkAssemblyWorkflow, _coreVersion);
                if (_needsClient)
                    NuGetProcessor.InstallPackage(_dte, installer, project, _clientPackage, _clientVersion);

                ProjectWorker.ExcludeFolder(project, "bin");
                ProjectWorker.ExcludeFolder(project, "performance");

                if (_signAssembly)
                    Signing.GenerateKey(_dte, project, _destDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error Processing Template: " + ex.Message);
            }
        }

        private void ReadWizardData(string wizardData)
        {
            XmlReaderSettings settings = new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment };

            string el = "";

            using (XmlReader reader = XmlReader.Create(new StringReader(wizardData), settings))
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element:
                            el = reader.Name;
                            break;
                        case XmlNodeType.Text:
                            switch (el)
                            {
                                case "CRMProjectType":
                                    _crmProjectType = reader.Value;
                                    break;
                                case "NeedsCore":
                                    _needsCore = bool.Parse(reader.Value);
                                    break;
                                case "NeedsWorkflow":
                                    _needsWorkflow = bool.Parse(reader.Value);
                                    break;
                                case "NeedsClient":
                                    _needsClient = bool.Parse(reader.Value);
                                    break;
                                case "IsUnitTest":
                                    _isUnitTest = bool.Parse(reader.Value);
                                    break;
                                case "SignAssembly":
                                    _signAssembly = bool.Parse(reader.Value);
                                    break;
                            }
                            break;
                    }
                }
        }
    }
}