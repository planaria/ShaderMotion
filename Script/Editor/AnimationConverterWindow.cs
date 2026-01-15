using UnityEngine;
using UnityEditor;
using System.IO;

namespace ShaderMotion
{
    public class AnimationConverterWindow : EditorWindow
    {
        private GameObject targetAvatar;
        private string inputFilePath = "";
        private string outputPath = "Assets/Animations/ConvertedAnimation.anim";
        private bool applyHumanPose = true;
        private bool includeBlendShapes = true;
        private SkinnedMeshRenderer shapeRenderer;

        [MenuItem("Window/ShaderMotion/Animation Converter")]
        public static void ShowWindow()
        {
            GetWindow<AnimationConverterWindow>("Animation Converter");
        }

        private void OnGUI()
        {
            GUILayout.Label("Animation Converter", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            targetAvatar = EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(GameObject), true) as GameObject;

            EditorGUILayout.Space();
            GUILayout.Label("Input Settings", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            inputFilePath = EditorGUILayout.TextField("Input File", inputFilePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.OpenFilePanel("Select captured animation file", "", "anim_raw");
                if (!string.IsNullOrEmpty(path))
                {
                    inputFilePath = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            outputPath = EditorGUILayout.TextField("Output Path", outputPath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.SaveFilePanelInProject("Save animation", "ConvertedAnimation", "anim", "Save converted animation");
                if (!string.IsNullOrEmpty(path))
                {
                    outputPath = path;
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            GUILayout.Label("Conversion Settings", EditorStyles.boldLabel);

            applyHumanPose = EditorGUILayout.Toggle("Apply Human Pose", applyHumanPose);
            includeBlendShapes = EditorGUILayout.Toggle("Include Blend Shapes", includeBlendShapes);
            
            if (includeBlendShapes)
            {
                shapeRenderer = EditorGUILayout.ObjectField("Shape Renderer", shapeRenderer, typeof(SkinnedMeshRenderer), true) as SkinnedMeshRenderer;
            }

            EditorGUILayout.Space();

            GUI.enabled = CanConvert();
            if (GUILayout.Button("Convert Animation", GUILayout.Height(30)))
            {
                ConvertAnimation();
            }
            GUI.enabled = true;

            if (!CanConvert())
            {
                EditorGUILayout.HelpBox(GetValidationMessage(), MessageType.Warning);
            }
        }

        private bool CanConvert()
        {
            return targetAvatar != null &&
                   !string.IsNullOrEmpty(inputFilePath) &&
                   !string.IsNullOrEmpty(outputPath) &&
                   File.Exists(inputFilePath);
        }

        private string GetValidationMessage()
        {
            if (targetAvatar == null)
                return "Please select a target avatar.";
            if (string.IsNullOrEmpty(inputFilePath))
                return "Please specify an input file.";
            if (!File.Exists(inputFilePath))
                return "Input file does not exist.";
            if (string.IsNullOrEmpty(outputPath))
                return "Please specify an output path.";
            return "";
        }

        private void ConvertAnimation()
        {
            var settings = new AnimationConverter.ConversionSettings
            {
                targetAvatar = targetAvatar,
                outputPath = outputPath,
                inputFilePath = inputFilePath,
                applyHumanPose = applyHumanPose,
                includeBlendShapes = includeBlendShapes,
                shapeRenderer = shapeRenderer,
                resolution = new Vector2Int(80, 45),
                tileSize = new Vector3Int(2, 1, 3),
                tileRadix = 3
            };

            EditorUtility.DisplayProgressBar("Converting Animation", "Processing frames...", 0.5f);

            try
            {
                var animationClip = AnimationConverter.Convert(settings);

                if (animationClip != null)
                {
                    EditorGUIUtility.PingObject(animationClip);
                    EditorUtility.DisplayDialog("Success", $"Animation converted successfully!\nSaved to: {outputPath}", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("Error", "Failed to convert animation. Check console for details.", "OK");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }
}