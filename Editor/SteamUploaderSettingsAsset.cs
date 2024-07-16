using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Editor
{
    [CreateAssetMenu(menuName = "Ligofff/SteamUploaderSettingsAsset", fileName = "SteamUploaderSettingsAsset")]
    public class SteamUploaderSettingsAsset : ScriptableObject
    {
        [InfoBox("Content builder relative folder path need to be placed in the root of your project (Project/*contentBuilderFolder*)", InfoMessageType.Info)]
        public string contentBuilderDirectoryPath = "_steamContentBuilder";
        [InfoBox("Content builder relative folder path need to be placed in the root of your project (Project/*contentBuilderFolder*)", InfoMessageType.Info)]
        public string buildsDirectoryPath = "Builds";

        public List<string> scriptNames;

        public string steamGameId = "1234567";

        public bool openUrlAfterBuild = true;

        public string GetBuildVersion()
        {
            return Application.version + "_" + Guid.NewGuid().ToString().Substring(0, 5);
        }
    }
}