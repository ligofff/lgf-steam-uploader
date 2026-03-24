using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor
{
    public class SteamUploaderWindow : OdinEditorWindow
    {
        private static readonly Regex TimestampLikeFileNameRegex = new Regex(
            @"(\d{8}|\d{4}[-_]\d{2}[-_]\d{2}|\d{6}[-_]\d{6}|\d{10,})",
            RegexOptions.Compiled);

        public enum SetLiveOverrideMode
        {
            KeepFromVdf = 0,
            Override = 1,
            DisableForThisRun = 2
        }

        [TitleGroup("Setup")]
        public SteamUploaderSettingsAsset settings;

        [TitleGroup("Setup")]
        [ShowIf("@settings != null")]
        [ValueDropdown(nameof(GetAvailableScriptNames))]
        [ValidateInput(nameof(ValidateSettings), "Assign settings and script first.")]
        public string scriptName = "run_build.bat";

        [TitleGroup("Run Options")]
        public bool previewOnly;

        [TitleGroup("Run Options")]
        public SetLiveOverrideMode setLiveOverrideMode = SetLiveOverrideMode.KeepFromVdf;

        [TitleGroup("Run Options")]
        [ShowIf("@setLiveOverrideMode == SetLiveOverrideMode.Override")]
        public string setLiveOverrideValue = "development";

        [TitleGroup("Run Options")]
        public bool createGitTag = true;

        [TitleGroup("Run Options")]
        [ShowIf(nameof(createGitTag))]
        public string customGitTag = string.Empty;

        [TitleGroup("Run Options")]
        [ShowIf(nameof(createGitTag))]
        [InfoBox("When enabled, Steam upload success is kept even if git tag/push fails. Git failure will be logged as warning.", InfoMessageType.Info)]
        public bool treatGitTagFailureAsWarning = true;

        [TitleGroup("Resolved Paths"), ShowInInspector, ReadOnly]
        private string ContentBuilderPath => settings == null
            ? "<settings not set>"
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, settings.contentBuilderDirectoryPath));

        [TitleGroup("Resolved Paths"), ShowInInspector, ReadOnly]
        private string BuildExecutablePath => settings == null
            ? "<settings not set>"
            : Path.Combine(GetProjectRootPath(), settings.buildsDirectoryPath, $"{Application.productName}.exe");

        [TitleGroup("Resolved Paths"), ShowInInspector, ReadOnly]
        private string UploadScriptPath => settings == null
            ? "<settings not set>"
            : Path.Combine(ContentBuilderPath, scriptName ?? string.Empty);

        [TitleGroup("Resolved Paths"), ShowInInspector, ReadOnly]
        private string AppBuildPathFromBatch
        {
            get
            {
                if (settings == null || string.IsNullOrWhiteSpace(scriptName) || !File.Exists(UploadScriptPath))
                    return "<not available>";

                return TryResolveAppBuildScriptPathFromBatch(UploadScriptPath, out var appBuildPath)
                    ? appBuildPath
                    : "<unable to resolve +run_app_build>";
            }
        }

        [TitleGroup("Resolved Paths"), ShowInInspector, ReadOnly]
        private string SteamBuildsPageUrl => TryGetCurrentAppId(out var appId)
            ? $"https://partner.steamgames.com/apps/builds/{appId}"
            : "<unknown app id>";

        private readonly string logPrefix = "<color=yellow>[SteamUploader]</color> ";

        [MenuItem("Ligofff/Steam Uploader")]
        private static void OpenWindow()
        {
            GetWindow<SteamUploaderWindow>().Show();
        }

        [ButtonGroup("Actions"), Button(ButtonSizes.Medium)]
        private void Upload()
        {
            if (!ValidateSettings())
            {
                Debug.LogError($"{logPrefix}Settings validation failed.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Steam upload confirmation", $"Upload build to Steam?\n{scriptName}", "Ok", "Cancel"))
                return;

            var preflightReport = settings.runPreflightBeforeUpload
                ? RunPreflightChecksInternal(logToConsole: true)
                : new PreflightReport();

            if (!preflightReport.IsValid)
            {
                if (settings.runPreflightBeforeUpload && settings.blockUploadOnPreflightIssues)
                {
                    Debug.LogError($"{logPrefix}Upload aborted due to preflight issues.");
                    return;
                }

                if (!EditorUtility.DisplayDialog("Preflight issues", BuildPreflightDialogMessage(preflightReport), "Continue", "Cancel"))
                    return;
            }
            else if (preflightReport.HasWarnings && settings.confirmOnPreflightWarnings)
            {
                if (!EditorUtility.DisplayDialog("Preflight warnings", BuildPreflightDialogMessage(preflightReport), "Continue", "Cancel"))
                    return;
            }

            if (!Directory.Exists(ContentBuilderPath))
            {
                Debug.LogError($"{logPrefix}Content builder folder not found: {ContentBuilderPath}");
                return;
            }

            var buildExecutablePath = BuildExecutablePath;
            if (!File.Exists(buildExecutablePath))
            {
                Debug.LogError($"{logPrefix}Build executable not found: {buildExecutablePath}");
                return;
            }

            var buildDirectoryPath = Path.GetDirectoryName(buildExecutablePath);
            if (string.IsNullOrWhiteSpace(buildDirectoryPath) || !Directory.Exists(buildDirectoryPath))
            {
                Debug.LogError($"{logPrefix}Build directory not found: {buildDirectoryPath}");
                return;
            }

            var moveToDir = Path.Combine(ContentBuilderPath, "content", "windows_content");
            if (settings.clearContentDirectoryBeforeCopy && Directory.Exists(moveToDir))
                Directory.Delete(moveToDir, true);

            Directory.CreateDirectory(moveToDir);
            CopyFilesRecursively(buildDirectoryPath, moveToDir);
            Debug.Log($"{logPrefix}Copied: {buildDirectoryPath} -> {moveToDir}");

            if (!File.Exists(UploadScriptPath))
            {
                Debug.LogError($"{logPrefix}Batch script not found: {UploadScriptPath}");
                return;
            }

            if (!TryResolveAppBuildScriptPathFromBatch(UploadScriptPath, out var baseAppBuildScriptPath))
            {
                Debug.LogError($"{logPrefix}Could not resolve +run_app_build from script: {UploadScriptPath}");
                return;
            }

            if (!File.Exists(baseAppBuildScriptPath))
            {
                Debug.LogError($"{logPrefix}Resolved app build script not found: {baseAppBuildScriptPath}");
                return;
            }

            var runAppBuildPath = PrepareEffectiveAppBuildScript(baseAppBuildScriptPath);
            var gitTag = ResolveGitTagForRun();

            Debug.Log($"{logPrefix}Root path: {Environment.CurrentDirectory}");
            Debug.Log($"{logPrefix}Build exe: {buildExecutablePath}");
            Debug.Log($"{logPrefix}Upload script: {UploadScriptPath}");
            Debug.Log($"{logPrefix}App build script for this run: {runAppBuildPath}");
            Debug.Log($"{logPrefix}Git tag for this run: {(string.IsNullOrWhiteSpace(gitTag) ? "<disabled>" : gitTag)}");

            var processInfo = new ProcessStartInfo(UploadScriptPath)
            {
                WorkingDirectory = ContentBuilderPath,
                Arguments = $"{QuoteArgument(gitTag)} {QuoteArgument(runAppBuildPath)}"
            };

            using var steamBuildScript = Process.Start(processInfo);
            if (steamBuildScript == null)
            {
                Debug.LogError($"{logPrefix}Process failed to start: {UploadScriptPath}");
                return;
            }

            var uploadStartedAtUtc = DateTime.UtcNow;
            Debug.Log($"{logPrefix}Steam builder started: {steamBuildScript.StartInfo.FileName}");
            steamBuildScript.WaitForExit();

            var exitCode = steamBuildScript.ExitCode;
            var logSummary = LogLatestBuildLogs(uploadStartedAtUtc);

            if (settings.openOutputFolderAfterUpload)
                OpenPath(Path.Combine(ContentBuilderPath, "output"));

            if (exitCode != 0)
            {
                if (!logSummary.HasAnyFreshLogs)
                    Debug.LogWarning($"{logPrefix}No new Steam build logs detected after this run. The batch likely failed before SteamCMD upload (for example branch gate).");

                if (exitCode == 3)
                    Debug.LogWarning($"{logPrefix}Exit code 3 is also used by branch gate in provided batch scripts.");

                if (logSummary.AppCommitFailed && logSummary.DepotManifestUploaded)
                {
                    Debug.LogWarning(
                        $"{logPrefix}Depot upload succeeded, but app build commit failed. " +
                        "This usually means Steamworks app-level commit/publish restrictions, not content transfer failure.");
                }

                if (IsGitTagFailureExitCode(exitCode))
                {
                    LogLatestGitError(uploadStartedAtUtc);

                    if (logSummary.AppBuildSucceeded && treatGitTagFailureAsWarning)
                    {
                        Debug.LogWarning(
                            $"{logPrefix}Steam build succeeded, but git tag step failed (exit code {exitCode}). " +
                            "Build upload is already complete. Check output/last_git_error.log for git details.");

                        if (settings.openUrlAfterBuild &&
                            SteamBuildsPageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            Application.OpenURL(SteamBuildsPageUrl);
                        }

                        return;
                    }
                }

                Debug.LogError($"{logPrefix}Steam builder failed with exit code {exitCode}.");
                return;
            }

            if (settings.openUrlAfterBuild && SteamBuildsPageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                Application.OpenURL(SteamBuildsPageUrl);

            Debug.Log($"{logPrefix}Steam builder completed successfully.");
        }

        [ButtonGroup("Actions")]
        private void RunPreflightChecks()
        {
            RunPreflightChecksInternal(logToConsole: true);
        }

        [ButtonGroup("Actions")]
        private void CreateExampleScript()
        {
            if (!ValidateSettings())
            {
                Debug.LogError($"{logPrefix}Settings validation failed.");
                return;
            }

            var appIdForTemplate = TryGetCurrentAppId(out var currentAppId) ? currentAppId : "YOUR_APP_ID";
            var defaultAppBuildPathForTemplate = ResolveDefaultAppBuildScriptPathForTemplate();

            var uploadScriptPath = UploadScriptPath;
            if (File.Exists(uploadScriptPath))
            {
                if (!EditorUtility.DisplayDialog("Overwrite confirmation", $"File {scriptName} already exists. Overwrite?", "Yes", "Cancel"))
                    return;

                File.Delete(uploadScriptPath);
            }

            var scriptString = ScriptTemplate
                .Replace("**yourGameID**", appIdForTemplate)
                .Replace("**projectName**", Application.productName)
                .Replace("**defaultAppBuildScriptPath**", defaultAppBuildPathForTemplate);

            File.WriteAllText(uploadScriptPath, scriptString, Encoding.UTF8);
            Debug.Log($"{logPrefix}Example script created: {uploadScriptPath}");
        }

        [ButtonGroup("Tools")]
        private void RefreshScriptNamesFromFolder()
        {
            if (settings == null)
            {
                Debug.LogError($"{logPrefix}Settings asset is not assigned.");
                return;
            }

            if (!Directory.Exists(ContentBuilderPath))
            {
                Debug.LogError($"{logPrefix}Content builder folder not found: {ContentBuilderPath}");
                return;
            }

            var names = Directory.GetFiles(ContentBuilderPath, "*.bat", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            settings.scriptNames = names;
            if (!names.Contains(scriptName, StringComparer.OrdinalIgnoreCase))
                scriptName = names.FirstOrDefault() ?? scriptName;

            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log($"{logPrefix}Script list refreshed. Found {names.Count} script(s).");
        }

        [ButtonGroup("Tools")]
        private void OpenContentBuilderFolder() => OpenPath(ContentBuilderPath);

        [ButtonGroup("Tools")]
        private void OpenAppBuildScript()
        {
            if (settings == null || string.IsNullOrWhiteSpace(scriptName))
                return;

            if (!File.Exists(UploadScriptPath))
            {
                Debug.LogWarning($"{logPrefix}Upload script not found: {UploadScriptPath}");
                return;
            }

            if (!TryResolveAppBuildScriptPathFromBatch(UploadScriptPath, out var appBuildPath))
            {
                Debug.LogWarning($"{logPrefix}Failed to resolve app build script from: {UploadScriptPath}");
                return;
            }

            OpenPath(appBuildPath);
        }

        [ButtonGroup("Tools")]
        private void OpenBuildsFolder()
        {
            if (settings == null) return;
            OpenPath(Path.Combine(GetProjectRootPath(), settings.buildsDirectoryPath));
        }

        [ButtonGroup("Tools")]
        private void OpenScriptsFolder()
        {
            if (settings == null) return;
            OpenPath(Path.Combine(ContentBuilderPath, "scripts"));
        }

        [ButtonGroup("Tools")]
        private void OpenOutputFolder()
        {
            if (settings == null) return;
            OpenPath(Path.Combine(ContentBuilderPath, "output"));
        }

        [ButtonGroup("Tools")]
        private void OpenLatestBuildLogsInExplorer()
        {
            if (settings == null) return;

            var outputPath = Path.Combine(ContentBuilderPath, "output");
            var latestLog = GetLatestFileByPattern(outputPath, "app_build_*.log")
                            ?? GetLatestFileByPattern(outputPath, "depot_build_*.log");

            if (string.IsNullOrWhiteSpace(latestLog))
            {
                Debug.LogWarning($"{logPrefix}No build logs were found in {outputPath}.");
                return;
            }

            OpenPath(latestLog);
        }

        [ButtonGroup("Tools")]
        private void OpenLastGitErrorLog()
        {
            if (settings == null) return;
            OpenPath(Path.Combine(ContentBuilderPath, "output", "last_git_error.log"));
        }

        [ButtonGroup("Tools")]
        private void OpenSteamBuildsPage()
        {
            if (!TryGetCurrentAppId(out _))
                return;

            Application.OpenURL(SteamBuildsPageUrl);
        }

        [ButtonGroup("Tools")]
        private void CopyResolvedInfoToClipboard()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"SettingsAsset: {(settings == null ? "<not set>" : settings.name)}");
            builder.AppendLine($"ScriptName: {scriptName}");
            builder.AppendLine($"ContentBuilderPath: {ContentBuilderPath}");
            builder.AppendLine($"BuildExecutablePath: {BuildExecutablePath}");
            builder.AppendLine($"UploadScriptPath: {UploadScriptPath}");
            builder.AppendLine($"AppBuildScriptPath: {AppBuildPathFromBatch}");
            builder.AppendLine($"SteamBuildsPage: {SteamBuildsPageUrl}");

            EditorGUIUtility.systemCopyBuffer = builder.ToString();
            Debug.Log($"{logPrefix}Resolved info copied to clipboard.");
        }

        private List<string> GetAvailableScriptNames()
        {
            var result = new List<string>();

            if (settings?.scriptNames != null && settings.scriptNames.Count > 0)
                result.AddRange(settings.scriptNames.Where(name => !string.IsNullOrWhiteSpace(name)));

            if (settings != null && Directory.Exists(ContentBuilderPath))
            {
                result.AddRange(
                    Directory.GetFiles(ContentBuilderPath, "*.bat", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool ValidateSettings()
        {
            if (settings == null) return false;
            if (string.IsNullOrWhiteSpace(scriptName)) return false;

            var availableScripts = GetAvailableScriptNames();
            return availableScripts.Count == 0 || availableScripts.Contains(scriptName, StringComparer.OrdinalIgnoreCase);
        }

        private PreflightReport RunPreflightChecksInternal(bool logToConsole)
        {
            var report = new PreflightReport();

            if (settings == null)
            {
                report.Errors.Add("Settings asset is not assigned.");
                return LogPreflight(report, logToConsole);
            }

            if (!Directory.Exists(ContentBuilderPath))
                report.Errors.Add($"Content builder folder does not exist: {ContentBuilderPath}");

            if (string.IsNullOrWhiteSpace(scriptName))
                report.Errors.Add("Script name is empty.");

            if (!File.Exists(BuildExecutablePath))
                report.Errors.Add($"Built executable not found: {BuildExecutablePath}");

            if (!File.Exists(UploadScriptPath))
                report.Errors.Add($"Upload script not found: {UploadScriptPath}");

            if (!File.Exists(UploadScriptPath))
                return LogPreflight(report, logToConsole);

            if (!TryResolveAppBuildScriptPathFromBatch(UploadScriptPath, out var appBuildPath))
            {
                report.Errors.Add($"Could not find +run_app_build in: {UploadScriptPath}");
                return LogPreflight(report, logToConsole);
            }

            if (!File.Exists(appBuildPath))
            {
                report.Errors.Add($"App build VDF not found: {appBuildPath}");
                return LogPreflight(report, logToConsole);
            }

            var appBuildText = File.ReadAllText(appBuildPath);

            if (!TryReadVdfValue(appBuildText, "AppID", out var appId) &&
                !TryReadVdfValue(appBuildText, "appid", out appId))
            {
                report.Errors.Add($"AppID is missing in app build script: {appBuildPath}");
            }

            if (TryReadVdfValue(appBuildText, "ContentRoot", out var appContentRootRaw))
            {
                var appContentRootPath = ResolvePathRelativeToFile(appBuildPath, appContentRootRaw);
                if (!Directory.Exists(appContentRootPath))
                    report.Errors.Add($"ContentRoot does not exist: {appContentRootPath}");

                ValidateDepotScripts(appBuildPath, appBuildText, appContentRootPath, report);
                AnalyzeSteamPipeRecommendations(appContentRootPath, report);
            }
            else
            {
                report.Errors.Add($"ContentRoot is missing in app build script: {appBuildPath}");
            }

            if (TryReadVdfValue(appBuildText, "Desc", out var description) &&
                description.IndexOf("uploaded from steamworks sdk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                report.Warnings.Add("Build description is generic. Consider setting a unique Desc for easier build tracking.");
            }

            if (TryReadVdfValue(appBuildText, "BuildOutput", out var buildOutputRaw))
            {
                var buildOutputPath = ResolvePathRelativeToFile(appBuildPath, buildOutputRaw);
                TryEnsureDirectoryExists(buildOutputPath, report);
            }
            else
            {
                report.Errors.Add($"BuildOutput is missing in app build script: {appBuildPath}");
            }

            return LogPreflight(report, logToConsole);
        }

        private void ValidateDepotScripts(string appBuildPath, string appBuildText, string appContentRootPath, PreflightReport report)
        {
            foreach (var depotPath in GetReferencedDepotScripts(appBuildPath, appBuildText))
            {
                if (!File.Exists(depotPath))
                {
                    report.Errors.Add($"Referenced depot script not found: {depotPath}");
                    continue;
                }

                var depotText = File.ReadAllText(depotPath);
                var depotContentRootPath = appContentRootPath;
                if (TryReadVdfValue(depotText, "ContentRoot", out var depotContentRootRaw))
                    depotContentRootPath = ResolvePathRelativeToFile(depotPath, depotContentRootRaw);

                if (!Directory.Exists(depotContentRootPath))
                {
                    report.Errors.Add($"Depot ContentRoot does not exist: {depotContentRootPath}");
                    continue;
                }

                foreach (var localPath in GetLocalPaths(depotText))
                {
                    if (!LocalPathLikelyExists(depotContentRootPath, localPath))
                        report.Errors.Add($"LocalPath from depot script does not resolve in content root: {localPath} (root: {depotContentRootPath})");
                }
            }
        }

        private static PreflightReport LogPreflight(PreflightReport report, bool logToConsole)
        {
            if (!logToConsole)
                return report;

            foreach (var warning in report.Warnings)
                Debug.LogWarning($"<color=yellow>[SteamUploader]</color> {warning}");

            if (report.IsValid && !report.HasWarnings)
            {
                Debug.Log("<color=yellow>[SteamUploader]</color> Preflight passed.");
                return report;
            }

            foreach (var error in report.Errors)
                Debug.LogError($"<color=yellow>[SteamUploader]</color> {error}");

            if (report.IsValid && report.HasWarnings)
                Debug.LogWarning("<color=yellow>[SteamUploader]</color> Preflight completed with warnings.");

            return report;
        }

        private string BuildPreflightDialogMessage(PreflightReport report)
        {
            var builder = new StringBuilder();
            if (report.Errors.Count > 0)
                builder.AppendLine($"{report.Errors.Count} error(s) found.");

            if (report.Warnings.Count > 0)
                builder.AppendLine($"{report.Warnings.Count} warning(s) found.");

            if (report.Errors.Count > 0)
                builder.AppendLine($"First error: {report.Errors[0]}");
            else if (report.Warnings.Count > 0)
                builder.AppendLine($"First warning: {report.Warnings[0]}");

            builder.Append("Continue upload?");
            return builder.ToString();
        }

        private void AnalyzeSteamPipeRecommendations(string contentRootPath, PreflightReport report)
        {
            if (!Directory.Exists(contentRootPath) || settings == null)
                return;

            var files = Directory.GetFiles(contentRootPath, "*", SearchOption.AllDirectories)
                .Where(path => !IsIgnoredDirectory(path))
                .ToArray();

            if (files.Length == 0)
            {
                report.Warnings.Add($"ContentRoot is empty: {contentRootPath}");
                return;
            }

            var maxSamplesPerRule = Math.Max(1, settings.maxWarningSamplesPerRule);

            if (settings.warnOnVeryLargeFiles)
            {
                var thresholdMb = Math.Max(1, settings.largeFileWarningThresholdMb);
                var thresholdBytes = thresholdMb * 1024L * 1024L;
                var largeFiles = files
                    .Select(path => new FileInfo(path))
                    .Where(info => info.Exists && info.Length >= thresholdBytes)
                    .OrderByDescending(info => info.Length)
                    .ToList();

                if (largeFiles.Count > 0)
                {
                    report.Warnings.Add(
                        $"Found {largeFiles.Count} file(s) >= {thresholdMb} MB. Large packed files can increase delta patch size.");

                    foreach (var fileInfo in largeFiles.Take(maxSamplesPerRule))
                    {
                        report.Warnings.Add(
                            $"Large file sample: {fileInfo.FullName} ({Math.Round(fileInfo.Length / 1024d / 1024d, 1)} MB)");
                    }
                }
            }

            if (settings.warnOnTimestampLikeFileNames)
            {
                var timestampFiles = files
                    .Where(path => TimestampLikeFileNameRegex.IsMatch(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                    .Take(maxSamplesPerRule + 1)
                    .ToList();

                if (timestampFiles.Count > 0)
                {
                    report.Warnings.Add(
                        "Detected file names that look timestamp/version generated. Unstable names can increase update size.");

                    foreach (var file in timestampFiles.Take(maxSamplesPerRule))
                        report.Warnings.Add($"Timestamp-like file sample: {file}");
                }
            }
        }

        private string PrepareEffectiveAppBuildScript(string baseAppBuildScriptPath)
        {
            var hasOverrides = previewOnly || setLiveOverrideMode != SetLiveOverrideMode.KeepFromVdf;
            if (!hasOverrides)
                return baseAppBuildScriptPath;

            var appBuildText = File.ReadAllText(baseAppBuildScriptPath);

            if (previewOnly)
                appBuildText = UpsertVdfValue(appBuildText, "Preview", "1");

            switch (setLiveOverrideMode)
            {
                case SetLiveOverrideMode.Override:
                    if (!string.IsNullOrWhiteSpace(setLiveOverrideValue))
                        appBuildText = UpsertVdfValue(appBuildText, "SetLive", setLiveOverrideValue.Trim());
                    break;
                case SetLiveOverrideMode.DisableForThisRun:
                    appBuildText = RemoveVdfValue(appBuildText, "SetLive");
                    break;
            }

            var outputDir = Path.GetDirectoryName(baseAppBuildScriptPath) ?? ContentBuilderPath;
            var fileName = $"{Path.GetFileNameWithoutExtension(baseAppBuildScriptPath)}_generated_{DateTime.Now:yyyyMMdd_HHmmss}.vdf";
            var generatedPath = Path.Combine(outputDir, fileName);
            File.WriteAllText(generatedPath, appBuildText, Encoding.UTF8);

            return generatedPath;
        }

        private string ResolveGitTagForRun()
        {
            if (!createGitTag)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(customGitTag))
                return customGitTag.Trim();

            return BuildDefaultGitTag();
        }

        private static string BuildDefaultGitTag()
        {
            var version = SanitizeGitTagToken(Application.version);
            return $"SteamUpload_v{version}_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private static string SanitizeGitTagToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            var sanitized = Regex.Replace(value, @"[^A-Za-z0-9._-]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
        }

        private bool TryGetCurrentAppId(out string appId)
        {
            appId = null;

            if (!TryGetCurrentAppBuildScriptPath(out var appBuildPath))
                return false;

            if (!File.Exists(appBuildPath))
                return false;

            var appBuildText = File.ReadAllText(appBuildPath);
            return TryReadVdfValue(appBuildText, "AppID", out appId) ||
                   TryReadVdfValue(appBuildText, "appid", out appId);
        }

        private bool TryGetCurrentAppBuildScriptPath(out string appBuildPath)
        {
            appBuildPath = null;

            if (settings == null || string.IsNullOrWhiteSpace(scriptName))
                return false;

            if (!File.Exists(UploadScriptPath))
                return false;

            if (!TryResolveAppBuildScriptPathFromBatch(UploadScriptPath, out var resolvedPath))
                return false;

            appBuildPath = resolvedPath;
            return !string.IsNullOrWhiteSpace(appBuildPath);
        }

        private string ResolveDefaultAppBuildScriptPathForTemplate()
        {
            if (TryGetCurrentAppBuildScriptPath(out var currentPath))
                return ConvertToPathRelativeToContentBuilder(currentPath);

            var scriptsDir = Path.Combine(ContentBuilderPath, "scripts");
            if (Directory.Exists(scriptsDir))
            {
                var firstAppBuild = Directory.GetFiles(scriptsDir, "app_build_*.vdf", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(firstAppBuild))
                    return ConvertToPathRelativeToContentBuilder(firstAppBuild);
            }

            return "scripts\\app_build.vdf";
        }

        private string ConvertToPathRelativeToContentBuilder(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
                return "scripts\\app_build.vdf";

            var builderPath = Path.GetFullPath(ContentBuilderPath).TrimEnd('\\', '/');
            var fullPath = Path.GetFullPath(absolutePath);
            var comparison = StringComparison.OrdinalIgnoreCase;

            if (fullPath.Equals(builderPath, comparison))
                return ".";

            var prefix = builderPath + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, comparison))
            {
                var relative = fullPath.Substring(prefix.Length);
                return relative.Replace('/', '\\');
            }

            return Path.GetFileName(fullPath);
        }

        private static bool TryResolveAppBuildScriptPathFromBatch(string batchPath, out string appBuildPath)
        {
            appBuildPath = null;
            var batchText = File.ReadAllText(batchPath);

            var match = Regex.Match(batchText,
                @"\+run_app_build\s+(?:""(?<path>[^""]+)""|(?<path>[^\s]+))",
                RegexOptions.IgnoreCase);

            if (!match.Success)
                return false;

            var rawPath = match.Groups["path"].Value.Trim();
            if (string.Equals(rawPath, "%APP_BUILD_SCRIPT%", StringComparison.OrdinalIgnoreCase))
            {
                var defaultPathMatch = Regex.Match(
                    batchText,
                    @"(?im)^\s*if\s+""%APP_BUILD_SCRIPT%""==""""\s+set\s+""APP_BUILD_SCRIPT=(?<path>[^""]+)""");

                if (defaultPathMatch.Success)
                    rawPath = defaultPathMatch.Groups["path"].Value.Trim();
            }

            rawPath = Environment.ExpandEnvironmentVariables(rawPath);
            if (string.IsNullOrWhiteSpace(rawPath))
                return false;

            var batchDirectory = Path.GetDirectoryName(batchPath) ?? string.Empty;
            if (Path.IsPathRooted(rawPath))
            {
                appBuildPath = Path.GetFullPath(rawPath);
                return true;
            }

            var directRelativePath = Path.GetFullPath(Path.Combine(batchDirectory, rawPath));
            if (File.Exists(directRelativePath))
            {
                appBuildPath = directRelativePath;
                return true;
            }

            // Legacy scripts may contain "..\scripts\app_build_*.vdf" although scripts are in contentBuilder/scripts.
            if (rawPath.StartsWith("..\\", StringComparison.Ordinal) || rawPath.StartsWith("../", StringComparison.Ordinal))
            {
                var trimmedPath = rawPath.Substring(3);
                var legacyFixedPath = Path.GetFullPath(Path.Combine(batchDirectory, trimmedPath));
                if (File.Exists(legacyFixedPath))
                {
                    appBuildPath = legacyFixedPath;
                    return true;
                }
            }

            appBuildPath = directRelativePath;

            return true;
        }

        private static string ResolvePathRelativeToFile(string sourceFilePath, string relativeOrAbsolutePath)
        {
            if (Path.IsPathRooted(relativeOrAbsolutePath))
                return Path.GetFullPath(relativeOrAbsolutePath);

            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFilePath) ?? string.Empty, relativeOrAbsolutePath));
        }

        private static bool TryReadVdfValue(string vdfText, string key, out string value)
        {
            value = null;
            var match = Regex.Match(vdfText, $@"(?im)^\s*""{Regex.Escape(key)}""\s*""(?<value>[^""]*)""");
            if (!match.Success)
                return false;

            value = match.Groups["value"].Value;
            return true;
        }

        private static string UpsertVdfValue(string vdfText, string key, string value)
        {
            var keyPattern = $@"(?im)^\s*""{Regex.Escape(key)}""\s*""[^""]*"".*$";
            if (Regex.IsMatch(vdfText, keyPattern))
                return Regex.Replace(vdfText, keyPattern, $"    \"{key}\" \"{value}\"");

            var appIdPattern = @"(?im)^\s*""AppID""\s*""[^""]*"".*$";
            if (Regex.IsMatch(vdfText, appIdPattern))
                return Regex.Replace(vdfText, appIdPattern, match => $"{match.Value}{Environment.NewLine}    \"{key}\" \"{value}\"");

            var appIdLowerPattern = @"(?im)^\s*""appid""\s*""[^""]*"".*$";
            if (Regex.IsMatch(vdfText, appIdLowerPattern))
                return Regex.Replace(vdfText, appIdLowerPattern, match => $"{match.Value}{Environment.NewLine}    \"{key}\" \"{value}\"");

            return $"{vdfText}{Environment.NewLine}    \"{key}\" \"{value}\"";
        }

        private static string RemoveVdfValue(string vdfText, string key)
        {
            var keyPattern = $@"(?im)^\s*""{Regex.Escape(key)}""\s*""[^""]*"".*\r?\n?";
            return Regex.Replace(vdfText, keyPattern, string.Empty);
        }

        private static IEnumerable<string> GetReferencedDepotScripts(string appBuildPath, string appBuildText)
        {
            var appBuildDirectory = Path.GetDirectoryName(appBuildPath) ?? string.Empty;
            var matches = Regex.Matches(appBuildText, @"(?im)^\s*""\d+""\s*""(?<depot>[^""]+\.vdf)""");

            foreach (Match match in matches)
            {
                var depotPath = match.Groups["depot"].Value.Trim();
                if (string.IsNullOrWhiteSpace(depotPath))
                    continue;

                yield return Path.IsPathRooted(depotPath)
                    ? Path.GetFullPath(depotPath)
                    : Path.GetFullPath(Path.Combine(appBuildDirectory, depotPath));
            }
        }

        private static IEnumerable<string> GetLocalPaths(string depotText)
        {
            var matches = Regex.Matches(depotText, @"(?im)^\s*""LocalPath""\s*""(?<path>[^""]+)""");
            foreach (Match match in matches)
            {
                var localPath = match.Groups["path"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(localPath))
                    yield return localPath;
            }
        }

        private static bool LocalPathLikelyExists(string contentRootPath, string localPathPattern)
        {
            var sanitized = localPathPattern.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            sanitized = sanitized.TrimStart('.', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(sanitized))
                return false;

            var wildcardIndex = sanitized.IndexOfAny(new[] { '*', '?' });
            var nonWildcardPrefix = wildcardIndex >= 0 ? sanitized.Substring(0, wildcardIndex) : sanitized;

            if (string.IsNullOrWhiteSpace(nonWildcardPrefix))
                return Directory.EnumerateFileSystemEntries(contentRootPath).Any();

            if (Path.HasExtension(nonWildcardPrefix))
                nonWildcardPrefix = Path.GetDirectoryName(nonWildcardPrefix) ?? string.Empty;

            var pathToCheck = Path.Combine(contentRootPath, nonWildcardPrefix);
            return Directory.Exists(pathToCheck) || File.Exists(pathToCheck);
        }

        private static void TryEnsureDirectoryExists(string path, PreflightReport report)
        {
            try
            {
                if (!Directory.Exists(path))
                    Directory.CreateDirectory(path);
            }
            catch (Exception exception)
            {
                report.Errors.Add($"Failed to create/check BuildOutput directory: {path}. {exception.Message}");
            }
        }

        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            foreach (var dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories)
                         .Where(dir => !IsIgnoredDirectory(dir)))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            foreach (var newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                         .Where(filepath => !IsIgnoredDirectory(filepath)))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
        }

        private static bool IsIgnoredDirectory(string path)
        {
            if (path.IndexOf("ButDontShipItWithYourGame", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (path.IndexOf("DoNotShip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private BuildLogSummary LogLatestBuildLogs(DateTime notBeforeUtc)
        {
            if (settings == null)
                return new BuildLogSummary();

            var outputPath = Path.Combine(ContentBuilderPath, "output");
            var latestAppLog = GetLatestFileByPattern(outputPath, "app_build_*.log");
            var latestDepotLog = GetLatestFileByPattern(outputPath, "depot_build_*.log");
            var summary = new BuildLogSummary();

            if (!string.IsNullOrWhiteSpace(latestAppLog) && IsFileFresh(latestAppLog, notBeforeUtc))
            {
                summary.HasAnyFreshLogs = true;
                summary.FreshAppLogPath = latestAppLog;
                LogLogTail(latestAppLog, 15);
            }

            if (!string.IsNullOrWhiteSpace(latestDepotLog) && IsFileFresh(latestDepotLog, notBeforeUtc))
            {
                summary.HasAnyFreshLogs = true;
                summary.FreshDepotLogPath = latestDepotLog;
                LogLogTail(latestDepotLog, 15);
            }

            if (!string.IsNullOrWhiteSpace(summary.FreshAppLogPath))
            {
                var appText = SafeReadAllText(summary.FreshAppLogPath);
                summary.AppCommitFailed = appText.IndexOf("Failed to commit build for AppID", StringComparison.OrdinalIgnoreCase) >= 0;
                summary.AppBuildSucceeded = appText.IndexOf("Successfully finished AppID", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (!string.IsNullOrWhiteSpace(summary.FreshDepotLogPath))
            {
                var depotText = SafeReadAllText(summary.FreshDepotLogPath);
                summary.DepotManifestUploaded = depotText.IndexOf("Success! New manifestID", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return summary;
        }

        private void LogLatestGitError(DateTime notBeforeUtc)
        {
            var gitLogPath = Path.Combine(ContentBuilderPath, "output", "last_git_error.log");
            if (!IsFileFresh(gitLogPath, notBeforeUtc))
                return;

            LogLogTail(gitLogPath, 25);
        }

        private static bool IsGitTagFailureExitCode(int exitCode)
        {
            return exitCode == 4 || exitCode == 41 || exitCode == 42;
        }

        private static bool IsFileFresh(string filePath, DateTime notBeforeUtc)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var fileTimeUtc = File.GetLastWriteTimeUtc(filePath);
            // Small tolerance for filesystem timestamp granularity.
            return fileTimeUtc >= notBeforeUtc.AddSeconds(-2);
        }

        private static string SafeReadAllText(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private void LogLogTail(string path, int lineCount)
        {
            if (!File.Exists(path))
                return;

            var lines = File.ReadAllLines(path);
            var start = Math.Max(0, lines.Length - lineCount);
            var tail = string.Join(Environment.NewLine, lines.Skip(start));
            Debug.Log($"{logPrefix}Latest log tail ({Path.GetFileName(path)}):{Environment.NewLine}{tail}");
        }

        private static string GetLatestFileByPattern(string rootPath, string pattern)
        {
            if (!Directory.Exists(rootPath))
                return null;

            return Directory.GetFiles(rootPath, pattern, SearchOption.AllDirectories)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }

        private void OpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Debug.LogWarning($"{logPrefix}Path not found: {path}");
                return;
            }

            EditorUtility.RevealInFinder(path);
        }

        private static string QuoteArgument(string value)
        {
            var safe = value ?? string.Empty;
            safe = safe.Replace("\"", "\\\"");
            return $"\"{safe}\"";
        }

        private static string GetProjectRootPath()
        {
            var projectRoot = Directory.GetParent(Application.dataPath);
            return projectRoot == null ? Environment.CurrentDirectory : projectRoot.FullName;
        }

        private string ScriptTemplate => @"@echo off
setlocal
cd /d ""%~dp0""

set ""BUILD_TAG=%~1""
set ""APP_BUILD_SCRIPT=%~2""
set ""GIT_ERROR_LOG=%~dp0output\last_git_error.log""
if ""%APP_BUILD_SCRIPT%""=="""" set ""APP_BUILD_SCRIPT=**defaultAppBuildScriptPath**""
if exist ""%GIT_ERROR_LOG%"" del ""%GIT_ERROR_LOG%"" >nul 2>&1

cd /d ""..""
for /f %%i in ('git rev-parse --abbrev-ref HEAD') do set BRANCHNAME=%%i
echo Branchname is %BRANCHNAME%

if /I ""%BRANCHNAME%""==""main"" goto UPLOAD
if /I ""%BRANCHNAME%""==""master"" goto UPLOAD
if /I ""%BRANCHNAME%""==""dev"" goto UPLOAD
goto ERROREXIT

:UPLOAD
echo Uploading to steam!
cd /d ""%~dp0""
builder\steamcmd.exe +login **yourLogin** **yourPassword** +drm_wrap **yourGameID** ""..\content\windows_content\**projectName**.exe"" ""..\content\windows_content\**projectName**.exe"" drmtoolp 0 +run_app_build ""%APP_BUILD_SCRIPT%"" +quit
set ""STEAM_RESULT=%ERRORLEVEL%""
echo Steam uploading completed (With success or fail)

if not ""%STEAM_RESULT%""==""0"" goto UPLOADFAILED
if ""%BUILD_TAG%""=="""" goto SUCCESS

cd /d ""..""
git tag ""%BUILD_TAG%"" > ""%GIT_ERROR_LOG%"" 2>&1
if errorlevel 1 goto GITTAGCREATEFAILED
git push --tags >> ""%GIT_ERROR_LOG%"" 2>&1
if errorlevel 1 goto GITTAGPUSHFAILED
echo Git tag %BUILD_TAG% created!
goto SUCCESS

:ERROREXIT
echo Branch isnt master/main/dev! Close process.
exit /b 3

:UPLOADFAILED
echo Steam upload failed with exit code %STEAM_RESULT%.
exit /b %STEAM_RESULT%

:GITTAGCREATEFAILED
echo Git tag create failed. See %GIT_ERROR_LOG%
exit /b 41

:GITTAGPUSHFAILED
echo Git tag push failed. See %GIT_ERROR_LOG%
exit /b 42

:SUCCESS
echo Exit...
exit /b 0
        ";

        private sealed class PreflightReport
        {
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public bool IsValid => Errors.Count == 0;
            public bool HasWarnings => Warnings.Count > 0;
        }

        private sealed class BuildLogSummary
        {
            public bool HasAnyFreshLogs;
            public string FreshAppLogPath;
            public string FreshDepotLogPath;
            public bool AppCommitFailed;
            public bool AppBuildSucceeded;
            public bool DepotManifestUploaded;
        }
    }
}
