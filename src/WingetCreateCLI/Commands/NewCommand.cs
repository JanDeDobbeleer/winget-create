﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

namespace Microsoft.WingetCreateCLI.Commands
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using CommandLine;
    using CommandLine.Text;
    using Microsoft.WingetCreateCLI.Logging;
    using Microsoft.WingetCreateCLI.Properties;
    using Microsoft.WingetCreateCLI.Telemetry;
    using Microsoft.WingetCreateCLI.Telemetry.Events;
    using Microsoft.WingetCreateCore;
    using Microsoft.WingetCreateCore.Common;
    using Microsoft.WingetCreateCore.Common.Exceptions;
    using Microsoft.WingetCreateCore.Models;
    using Microsoft.WingetCreateCore.Models.DefaultLocale;
    using Microsoft.WingetCreateCore.Models.Installer;
    using Microsoft.WingetCreateCore.Models.Version;
    using Newtonsoft.Json;
    using Sharprompt;

    /// <summary>
    /// Command to launch a wizard that prompt users for information to generate a new manifest.
    /// </summary>
    [Verb("new", HelpText = "NewCommand_HelpText", ResourceType = typeof(Resources))]
    public class NewCommand : BaseCommand
    {
        /// <summary>
        /// The url path to the manifest documentation site.
        /// </summary>
        private const string ManifestDocumentationUrl = "https://github.com/microsoft/winget-cli/blob/master/doc/ManifestSpecv1.0.md";

        /// <summary>
        /// Installer types for which we can trust that the detected architecture is correct, so don't need to prompt the user to confirm.
        /// </summary>
        private static readonly InstallerType[] ReliableArchitectureInstallerTypes = new[] { InstallerType.Msix, InstallerType.Appx };

        /// <summary>
        /// Gets the usage examples for the New command.
        /// </summary>
        [Usage(ApplicationAlias = ProgramApplicationAlias)]
        public static IEnumerable<Example> Examples
        {
            get
            {
                yield return new Example(Resources.Example_NewCommand_StartFromScratch, new NewCommand { });
                yield return new Example(Resources.Example_NewCommand_DownloadInstaller, new NewCommand { InstallerUrls = new string[] { "<InstallerUrl1>", "<InstallerUrl2>, .." } });
                yield return new Example(Resources.Example_NewCommand_SaveLocallyOrSubmit, new NewCommand
                {
                    InstallerUrls = new string[] { "<InstallerUrl1>", "<InstallerUrl2>, .." },
                    OutputDir = "<OutputDirectory>",
                    GitHubToken = "<GitHubPersonalAccessToken>",
                });
            }
        }

        /// <summary>
        /// Gets or sets the installer URL(s) used for downloading and parsing the installer file(s).
        /// </summary>
        [Value(0, MetaName = "urls", Required = false, HelpText = "InstallerUrl_HelpText", ResourceType = typeof(Resources))]
        public IEnumerable<string> InstallerUrls { get; set; }

        /// <summary>
        /// Gets or sets the outputPath where the generated manifest file should be saved to.
        /// </summary>
        [Option('o', "out", Required = false, HelpText = "OutputDirectory_HelpText", ResourceType = typeof(Resources))]
        public string OutputDir { get; set; }

        /// <summary>
        /// Gets or sets the GitHub token used to submit a pull request on behalf of the user.
        /// </summary>
        [Option('t', "token", Required = false, HelpText = "GitHubToken_HelpText", ResourceType = typeof(Resources))]
        public override string GitHubToken { get => base.GitHubToken; set => base.GitHubToken = value; }

        /// <summary>
        /// Executes the new command flow.
        /// </summary>
        /// <returns>Boolean representing success or fail of the command.</returns>
        public override async Task<bool> Execute()
        {
            CommandExecutedEvent commandEvent = new CommandExecutedEvent
            {
                Command = nameof(NewCommand),
                InstallerUrl = string.Join(',', this.InstallerUrls),
                HasGitHubToken = !string.IsNullOrEmpty(this.GitHubToken),
            };

            try
            {
                Prompt.Symbols.Done = new Symbol(string.Empty, string.Empty);
                Prompt.Symbols.Prompt = new Symbol(string.Empty, string.Empty);

                Manifests manifests = new Manifests();

                if (!this.InstallerUrls.Any())
                {
                    PromptHelper.PromptPropertyAndSetValue(this, nameof(this.InstallerUrls), this.InstallerUrls, minimum: 1, validationModel: new Installer(), validationName: nameof(Installer.InstallerUrl));
                    Console.Clear();
                }

                List<InstallerMetadata> installerUpdateList = new List<InstallerMetadata>();

                foreach (var installerUrl in this.InstallerUrls)
                {
                    string packageFile = await DownloadPackageFile(installerUrl);
                    if (string.IsNullOrEmpty(packageFile))
                    {
                        return false;
                    }

                    installerUpdateList.Add(new InstallerMetadata { InstallerUrl = installerUrl, PackageFile = packageFile });
                }

                try
                {
                    PackageParser.ParsePackages(installerUpdateList, manifests);
                    DisplayMismatchedArchitectures(installerUpdateList);
                }
                catch (IOException iOException) when (iOException.HResult == -2147024671)
                {
                    Logger.ErrorLocalized(nameof(Resources.DefenderVirus_ErrorMessage));
                    return false;
                }
                catch (ParsePackageException parsePackageException)
                {
                    parsePackageException.ParseFailedInstallerUrls.ForEach(i => Logger.ErrorLocalized(nameof(Resources.PackageParsing_Error), i));
                    return false;
                }

                Console.WriteLine();
                Console.WriteLine(Resources.NewCommand_Header);
                Console.WriteLine();
                Logger.InfoLocalized(nameof(Resources.ManifestDocumentation_HelpText), ManifestDocumentationUrl);
                Console.WriteLine();
                Console.WriteLine(Resources.NewCommand_Description);
                Console.WriteLine();

                Logger.DebugLocalized(nameof(Resources.EnterFollowingFields_Message));

                bool isManifestValid;

                do
                {
                    if (this.WingetRepoOwner == DefaultWingetRepoOwner &&
                        this.WingetRepo == DefaultWingetRepo)
                    {
                        if (!await this.PromptPackageIdentifierAndCheckDuplicates(manifests))
                        {
                            Console.WriteLine();
                            Logger.ErrorLocalized(nameof(Resources.PackageIdAlreadyExists_Error));
                            return false;
                        }
                    }

                    PromptPropertiesAndDisplayManifests(manifests);
                    RemoveEmptyStringFieldsInManifests(manifests);
                    isManifestValid = ValidateManifestsInTempDir(manifests);
                }
                while (Prompt.Confirm(Resources.ConfirmManifestCreation_Message));

                if (string.IsNullOrEmpty(this.OutputDir))
                {
                    this.OutputDir = Directory.GetCurrentDirectory();
                }

                SaveManifestDirToLocalPath(manifests, this.OutputDir);

                if (isManifestValid && Prompt.Confirm(Resources.ConfirmGitHubSubmitManifest_Message))
                {
                    if (await this.LoadGitHubClient(true))
                    {
                        return commandEvent.IsSuccessful = await this.GitHubSubmitManifests(manifests);
                    }

                    return false;
                }
                else
                {
                    Console.WriteLine();
                    Logger.WarnLocalized(nameof(Resources.SkippingPullRequest_Message));
                    return commandEvent.IsSuccessful = isManifestValid;
                }
            }
            finally
            {
                TelemetryManager.Log.WriteEvent(commandEvent);
            }
        }

        private static void PromptPropertiesAndDisplayManifests(Manifests manifests)
        {
            PromptRequiredProperties(manifests.VersionManifest);
            PromptRequiredProperties(manifests.InstallerManifest, manifests.VersionManifest);
            PromptRequiredProperties(manifests.DefaultLocaleManifest, manifests.VersionManifest);

            Console.WriteLine();
            if (Prompt.Confirm(Resources.ModifyOptionalDefaultLocaleFields_Message))
            {
                PromptOptionalProperties(manifests.DefaultLocaleManifest);
            }

            if (Prompt.Confirm(Resources.ModifyOptionalInstallerFields_Message))
            {
                PromptHelper.DisplayInstallersAsMenuSelection(manifests.InstallerManifest);
            }

            Console.WriteLine();
            DisplayManifestPreview(manifests);
        }

        private static void PromptRequiredProperties<T>(T manifest, VersionManifest versionManifest = null)
        {
            var properties = manifest.GetType().GetProperties().ToList();
            var requiredProperties = properties.Where(p => p.GetCustomAttribute<RequiredAttribute>() != null).ToList();

            foreach (var property in requiredProperties)
            {
                if (property.PropertyType.IsGenericType)
                {
                    // Generic logic for handling nested object models
                    Type itemType = property.GetValue(manifest).GetType().GetGenericArguments().Single();

                    if (itemType.Name == nameof(Installer))
                    {
                        PromptInstallerProperties(manifest, property);
                    }
                }
                else if (property.PropertyType.IsValueType || property.PropertyType == typeof(string))
                {
                    if (property.Name == nameof(VersionManifest.PackageIdentifier) ||
                        property.Name == nameof(VersionManifest.ManifestType) ||
                        property.Name == nameof(VersionManifest.ManifestVersion))
                    {
                        continue;
                    }

                    if (property.Name == nameof(VersionManifest.PackageVersion) && versionManifest != null)
                    {
                        property.SetValue(manifest, versionManifest.PackageVersion);
                        continue;
                    }

                    if (property.Name == nameof(DefaultLocaleManifest.PackageLocale) && versionManifest != null)
                    {
                        property.SetValue(manifest, versionManifest.DefaultLocale);
                        continue;
                    }

                    PromptHelper.PromptPropertyAndSetValue(manifest, property.Name, property.GetValue(manifest));
                    Logger.Trace($"Property [{property.Name}] set to the value [{property.GetValue(manifest)}]");
                }
            }
        }

        private static void PromptOptionalProperties<T>(T manifest)
        {
            var properties = manifest.GetType().GetProperties().ToList();
            var optionalProperties = properties.Where(p =>
                p.GetCustomAttribute<RequiredAttribute>() == null &&
                p.GetCustomAttribute<JsonPropertyAttribute>() != null).ToList();

            foreach (var property in optionalProperties)
            {
                Type type = property.PropertyType;
                if (type.IsEnumerable())
                {
                    Type elementType = type.GetGenericArguments().SingleOrDefault();
                    if (elementType.IsNonStringClassType() && !Prompt.Confirm(Resources.EditAgreements_Message))
                    {
                        continue;
                    }
                }

                PromptHelper.PromptPropertyAndSetValue(manifest, property.Name, property.GetValue(manifest));
                Logger.Trace($"Property [{property.Name}] set to the value [{property.GetValue(manifest)}]");
            }
        }

        private static void PromptInstallerProperties<T>(T manifest, PropertyInfo property)
        {
            List<Installer> installers = new List<Installer>((ICollection<Installer>)property.GetValue(manifest));
            foreach (var installer in installers)
            {
                var installerProperties = installer.GetType().GetProperties().ToList();

                var requiredInstallerProperties = installerProperties
                    .Where(p => Attribute.IsDefined(p, typeof(RequiredAttribute)) || p.Name == nameof(InstallerType)).ToList();

                bool prompted = false;

                // If we know the installertype is EXE, prompt the user for installer switches (silent and silentwithprogress)
                if (installer.InstallerType == InstallerType.Exe)
                {
                    Console.WriteLine();
                    Logger.DebugLocalized(nameof(Resources.AdditionalMetadataNeeded_Message), installer.InstallerUrl);
                    prompted = true;
                    PromptInstallerSwitchesForExe(manifest);
                }

                foreach (var requiredProperty in requiredInstallerProperties)
                {
                    var currentValue = requiredProperty.GetValue(installer);

                    // Only prompt if the value isn't already set, or if it's the Architecture property and we don't trust the parser to have gotten it correct for this InstallerType.
                    if (currentValue == null ||
                        (requiredProperty.Name == nameof(Installer.Architecture) && !ReliableArchitectureInstallerTypes.Contains(installer.InstallerType.Value)))
                    {
                        if (!prompted)
                        {
                            Console.WriteLine();
                            Logger.DebugLocalized(nameof(Resources.AdditionalMetadataNeeded_Message), installer.InstallerUrl);
                            prompted = true;
                        }

                        PromptHelper.PromptPropertyAndSetValue(installer, requiredProperty.Name, requiredProperty.GetValue(installer));
                    }
                }
            }
        }

        private static void PromptInstallerSwitchesForExe<T>(T manifest)
        {
            InstallerSwitches installerSwitches = new InstallerSwitches();

            PromptHelper.PromptPropertyAndSetValue(installerSwitches, nameof(InstallerSwitches.Silent), installerSwitches.Silent);
            PromptHelper.PromptPropertyAndSetValue(installerSwitches, nameof(InstallerSwitches.SilentWithProgress), installerSwitches.SilentWithProgress);

            if (!string.IsNullOrEmpty(installerSwitches.Silent) || !string.IsNullOrEmpty(installerSwitches.SilentWithProgress))
            {
                manifest.GetType().GetProperty(nameof(InstallerSwitches)).SetValue(manifest, installerSwitches);
            }
        }

        /// <summary>
        /// Prompts for the package identifier and checks if the package identifier already exists.
        /// If the package identifier is valid, the value is applied to the other manifests.
        /// </summary>
        /// <param name="manifests">Manifests object model.</param>
        /// <returns>Boolean value indicating whether the package identifier is valid.</returns>
        private async Task<bool> PromptPackageIdentifierAndCheckDuplicates(Manifests manifests)
        {
            VersionManifest versionManifest = manifests.VersionManifest;
            PromptHelper.PromptPropertyAndSetValue(versionManifest, nameof(versionManifest.PackageIdentifier), versionManifest.PackageIdentifier);

            string exactMatch;
            try
            {
                exactMatch = await this.GitHubClient.FindPackageId(versionManifest.PackageIdentifier);
            }
            catch (Octokit.RateLimitExceededException)
            {
                Logger.ErrorLocalized(nameof(Resources.RateLimitExceeded_Message));
                return false;
            }

            if (!string.IsNullOrEmpty(exactMatch))
            {
                return false;
            }
            else
            {
                manifests.InstallerManifest.PackageIdentifier = versionManifest.PackageIdentifier;
                manifests.DefaultLocaleManifest.PackageIdentifier = versionManifest.PackageIdentifier;
                return true;
            }
        }
    }
}
