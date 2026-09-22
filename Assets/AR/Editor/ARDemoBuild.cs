using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ARVDU.EditorTools
{
    /// <summary>
    /// Headless Android build entry point.
    /// <para>
    /// Unity has no built-in command-line build for Android -- only desktop targets build without
    /// an explicit method -- so building without the Editor GUI (CI, or simply when the Editor
    /// isn't reachable) needs this. Invoke with:
    /// </para>
    /// <code>
    /// unity build . --target Android -o Builds/ARImageTracking.apk \
    ///   --execute-method ARVDU.EditorTools.ARDemoBuild.BuildAndroid
    /// </code>
    /// </summary>
    public static class ARDemoBuild
    {
        const string k_DefaultOutput = "Builds/ARImageTracking.apk";

        public static void BuildAndroid()
        {
            var output = ResolveOutputPath();

            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
                throw new BuildFailedException("No enabled scenes in Build Settings -- nothing to build.");

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });

            var summary = report.summary;
            Debug.Log(
                $"[AR build] {summary.result}: {output} " +
                $"({summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} errors, " +
                $"{summary.totalWarnings} warnings)");

            // Throwing is what makes the CLI exit non-zero; BuildPlayer itself returns a report
            // rather than failing the process, so a silent bad build would otherwise look fine.
            if (summary.result != BuildResult.Succeeded)
                throw new BuildFailedException($"Android build {summary.result}.");
        }

        /// <summary>
        /// Reads the output path the CLI forwards as <c>-buildOutput</c> (that's what
        /// <c>unity build -o</c> becomes once <c>--execute-method</c> is in play; honouring it is
        /// the method's job, not Unity's).
        /// </summary>
        static string ResolveOutputPath()
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-buildOutput" && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1];
            }

            return k_DefaultOutput;
        }
    }
}
