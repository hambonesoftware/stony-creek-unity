using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace StonyCreek.EditorTools
{
    /// Batch entry points:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod StonyCreek.EditorTools.StonyBuild.BuildWeb -logFile web-build.log
    /// (no -nographics: tree impostors are rendered on the GPU)
    public static class StonyBuild
    {
        public static void BuildWorldOnly()
        {
            Settings();
            StonyWorldBuilder.Build();
        }

        [MenuItem("Stony Creek/Build Web")]
        public static void BuildWeb()
        {
            Settings();
            StonyWorldBuilder.Build();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { StonyWorldBuilder.ScenePath },
                locationPathName = "Builds/Web",
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Web build ended with " + report.summary.result);
            File.WriteAllText("Builds/Web/.nojekyll", string.Empty);
            Debug.Log($"Stony Creek web build complete ({report.summary.totalSize:N0} bytes).");
        }

        static void Settings()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.WebGL, BuildTarget.WebGL);
            PlayerSettings.productName = "Stony Creek Explorer";
            PlayerSettings.companyName = "Hambone Software";
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.template = "PROJECT:Stony";
            PlayerSettings.runInBackground = false;
            PlayerSettings.defaultWebScreenWidth = 1280;
            PlayerSettings.defaultWebScreenHeight = 720;
            UnityEditor.WebGL.UserBuildSettings.codeOptimization = UnityEditor.WebGL.WasmCodeOptimization.RuntimeSpeed;
            EditorUserBuildSettings.webGLBuildSubtarget = WebGLTextureSubtarget.DXT;
            AssetDatabase.SaveAssets();
        }
    }
}
