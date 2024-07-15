using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Editor
{
    public class SteamUploaderWindow : OdinEditorWindow
    {
        public SteamUploaderSettingsAsset settings;

        [Sirenix.OdinInspector.ShowIf("@settings != null")]
        [ValueDropdown("@settings.scriptNames"), ValidateInput("@ValidateSettings()")]
        public string scriptName = "run_build.bat";
        
        private string ContentBuilderPath => $"{Environment.CurrentDirectory}\\{settings.contentBuilderDirectoryPath}";
        private string logPrefix = "<color=yellow>[SteamUploader]</color> ";
        
        [MenuItem("Ligofff/Steam Uploader")]
        private static void OpenWindow()
        {
            GetWindow<SteamUploaderWindow>().Show();
        }

        [Button]
        private void CreateExampleScript()
        {
            if (!ValidateSettings()) return;
            
            var uploadScriptPath = $"{ContentBuilderPath}\\{scriptName}";

            if (File.Exists(uploadScriptPath))
            {
                if (EditorUtility.DisplayDialog("Overwrite confirmation", $"File {scriptName} already exists. Overwrite?",
                        "Yes", "Cancel"))
                {
                    File.Delete(uploadScriptPath);
                }
                else
                {
                    return;
                }
            }

            var scriptString = ScriptTemplate;

            scriptString = scriptString.Replace("**yourGameID**", settings.steamGameId);
            scriptString = scriptString.Replace("**projectName**", Application.productName);

            File.WriteAllText(uploadScriptPath, scriptString, System.Text.Encoding.UTF8);
        }

        private bool ValidateSettings()
        {
            if (settings == null) return false;
            if (!settings.scriptNames.Contains(scriptName)) return false;

            return true;
        }
        
        [Button]
        private void Upload()
        {
            if (!EditorUtility.DisplayDialog("Steam upload confirmation", $"Upload build to Steam?\n{scriptName}", "Ok", "Cancel")) return;

            if (!Directory.Exists(ContentBuilderPath))
            {
                Debug.LogError($"Content builder folder not found!");
                return;
            }

            var path = Application.dataPath.Substring(0, Application.dataPath.IndexOf("/Assets")) + $"/{settings.buildsDirectoryPath}/{Application.productName}.exe";
            var dir = path.Replace($"{Application.productName}.exe", "");

            Debug.Log($"{logPrefix}Root path: {Environment.CurrentDirectory}");
            Debug.Log($"{logPrefix}PathToBuiltProject: {path}");
            Debug.Log($"{logPrefix}Post build path: {dir}");

            var moveToDir = $"{ContentBuilderPath}\\content\\windows_content\\";
            // copy files into steam builder content
            Directory.Delete(moveToDir, true);
            // need copy all files and not use Directory.Move() as it will empty the build directory resulting in a failed build on UCB
            CopyFilesRecursively(dir, moveToDir);
            Debug.Log($"{logPrefix}Moved: {dir} to {moveToDir}");

            var uploadScriptPath = $"{ContentBuilderPath}\\{scriptName}";

            if (!File.Exists(uploadScriptPath))
            {
                Debug.LogError($"{logPrefix}Bat file not found at path {uploadScriptPath}!");
                return;
            }

            var buildVersion = settings.GetBuildVersion();
            
            var processInfo = new ProcessStartInfo(uploadScriptPath);
            processInfo.Arguments = $"SteamBuild.v.{buildVersion}";

            using var steamBuildScript = Process.Start(processInfo);

            if (steamBuildScript == null)
            {
                Debug.LogError($"{logPrefix}Process failed to start. {uploadScriptPath}");
                return;
            }

            Debug.Log($"{logPrefix}Steam builder started: {steamBuildScript.StartInfo.FileName}");

            steamBuildScript.WaitForExit();

            if (settings.openUrlAfterBuild)
                Application.OpenURL($"https://partner.steamgames.com/apps/builds/{settings.steamGameId}");
            
            Debug.Log($"{logPrefix}Steam builder completed.");
        }

        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            // Now Create all of the directories
            foreach (var dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories)
                         .Where(dir => !IsIgnoredDirectory(dir)))
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));

            // Copy all the files & Replaces any files with the same name
            foreach (var newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                         .Where(filepath => !IsIgnoredDirectory(filepath)))
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
        }

        private static bool IsIgnoredDirectory(string dir)
        {
            if (dir.Contains("ButDontShipItWithYourGame")) return true;
            if (dir.Contains("DoNotShip")) return true;

            return false;
        }

        private string ScriptTemplate => @"@echo off
cd %~dp0

cd ..\
for /f %%i in ('git rev-parse --abbrev-ref HEAD') do set BRANCHNAME=%%i
echo Branchname is %BRANCHNAME%

if %BRANCHNAME%==main or %BRANCHNAME%==master (goto UPLOAD) else (goto ERROREXIT)

:UPLOAD
echo Uploading to steam!
cd %~dp0
builder\steamcmd.exe +login **yourLogin** **yourPassword** +drm_wrap **yourGameID** ""..\content\windows_content\**projectName**.exe"" ""..\content\windows_content\**projectName**.exe"" drmtoolp 0 +run_app_build ..\scripts\app_build_**yourGameID**.vdf +quit
echo Steam uploading completed (With success or fail)
cd ..\
git tag %1
git push --tags
echo Git tag %1 created!
goto ENDBAT

:ERROREXIT
echo Branch isnt master or main! Close process.
goto ENDBAT

:ENDBAT
echo Exit...
Pause
        ";
    }
}