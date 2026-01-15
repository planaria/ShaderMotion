using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using Unity.Collections;
using System.Linq;

namespace ShaderMotion
{
    public class AnimationConverter
    {
        public struct ConversionSettings
        {
            public GameObject targetAvatar;
            public string outputPath;
            public string inputFilePath;
            public bool applyHumanPose;
            public bool includeBlendShapes;
            public SkinnedMeshRenderer shapeRenderer;
            public Vector2Int resolution;
            public Vector3Int tileSize;
            public int tileRadix;
        }

        public static AnimationClip Convert(ConversionSettings settings)
        {
            if (!ValidateSettings(settings))
                return null;

            using (var decoder = new CapturedAnimationDecoder(settings.inputFilePath))
            {
                var animator = settings.targetAvatar.GetComponent<Animator>();
                if (!animator)
                {
                    Debug.LogError("Target avatar must have an Animator component");
                    return null;
                }

                if (!animator.avatar)
                {
                    Debug.LogError("Animator must have an Avatar assigned");
                    return null;
                }

                if (!animator.avatar.isHuman && settings.applyHumanPose)
                {
                    Debug.LogError("Cannot apply human pose to non-humanoid avatar");
                    return null;
                }

                var skeleton = new Skeleton(animator);
                var morph = new Morph(animator);
                var layout = new MotionLayout(skeleton, morph);
                var motionDecoder = new MotionDecoder(skeleton, morph, layout,
                    settings.resolution.x, settings.resolution.y,
                    tileWidth: settings.tileSize.x, tileHeight: settings.tileSize.y,
                    tileDepth: settings.tileSize.z, tileRadix: settings.tileRadix);

                var animationClip = new AnimationClip();
                animationClip.frameRate = (float)decoder.FrameRate;

                if (animationClip.frameRate <= 0)
                {
                    Debug.LogError("Invalid frame rate in capture file");
                    return null;
                }

                // Initialize curves for humanoid animation
                var muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
                var rootPositionCurves = new AnimationCurve[3];
                var rootRotationCurves = new AnimationCurve[4];
                var blendShapeCurves = new Dictionary<string, AnimationCurve>();

                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    muscleCurves[i] = new AnimationCurve();
                for (int i = 0; i < 3; i++)
                    rootPositionCurves[i] = new AnimationCurve();
                for (int i = 0; i < 4; i++)
                    rootRotationCurves[i] = new AnimationCurve();

                if (settings.includeBlendShapes && settings.shapeRenderer != null)
                {
                    var mesh = settings.shapeRenderer.sharedMesh;
                    for (int i = 0; i < mesh.blendShapeCount; i++)
                    {
                        var shapeName = mesh.GetBlendShapeName(i);
                        blendShapeCurves[shapeName] = new AnimationCurve();
                    }
                }

                // Setup HumanPoseHandler and HumanPose exactly like MotionPlayer
                var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
                var humanPose = new HumanPose();
                poseHandler.GetHumanPose(ref humanPose);
                var swingTwists = new Vector3[HumanTrait.BoneCount];

                int frameCount = 0;
                CapturedFrame frame;

                while (decoder.TryRead(out frame))
                {
                    var inputTexture = CreateTextureFromFrameData(frame.data, settings.resolution.x, settings.resolution.y);
                    var processedTexture = ProcessWithVideoDecoder(inputTexture);
                    var gpuRequest = UnityEngine.Rendering.AsyncGPUReadback.Request(processedTexture);
                    gpuRequest.WaitForCompletion();
                    
                    motionDecoder.Update(gpuRequest, 0);
                    
                    Object.DestroyImmediate(inputTexture);
                    Object.DestroyImmediate(processedTexture);

                    float time = (float)frame.time;
                    var motions = motionDecoder.motions;

                    if (settings.applyHumanPose)
                    {
                        // Apply exact same logic as MotionPlayer.ApplyHumanPose()
                        System.Array.Resize(ref swingTwists, HumanTrait.BoneCount);
                        for (int i = 0; i < HumanTrait.BoneCount; i++)
                            swingTwists[i] = motions[i].t;

                        HumanPoser.SetBoneSwingTwists(ref humanPose, swingTwists);
                        HumanPoser.SetHipsPositionRotation(ref humanPose, motions[0].t, motions[0].q, motions[0].s);

                        // Record root motion
                        var (rootPos, rootRot) = HumanPoser.GetRootMotion(ref humanPose, animator);

                        AddLinearKey(rootPositionCurves[0], time, rootPos.x);
                        AddLinearKey(rootPositionCurves[1], time, rootPos.y);
                        AddLinearKey(rootPositionCurves[2], time, rootPos.z);

                        AddLinearKey(rootRotationCurves[0], time, rootRot.x);
                        AddLinearKey(rootRotationCurves[1], time, rootRot.y);
                        AddLinearKey(rootRotationCurves[2], time, rootRot.z);
                        AddLinearKey(rootRotationCurves[3], time, rootRot.w);

                        for (int i = 0; i < HumanTrait.MuscleCount; i++)
                        {
                            AddLinearKey(muscleCurves[i], time, humanPose.muscles[i]);
                        }
                    }

                    if (settings.includeBlendShapes)
                    {
                        foreach (var shape in motionDecoder.shapes)
                        {
                            if (blendShapeCurves.ContainsKey(shape.Key))
                            {
                                var weight = Mathf.Round(Mathf.Clamp01(shape.Value) * 100 / 0.1f) * 0.1f;
                                AddLinearKey(blendShapeCurves[shape.Key], time, weight);
                            }
                        }
                    }

                    frameCount++;
                }

                if (frameCount == 0)
                {
                    Debug.LogError("No valid frames found in capture file");
                    return null;
                }

                Debug.Log($"Processed {frameCount} frames successfully");

                // Set animation curves
                if (settings.applyHumanPose)
                {
                    animationClip.SetCurve("", typeof(Animator), "RootT.x", rootPositionCurves[0]);
                    animationClip.SetCurve("", typeof(Animator), "RootT.y", rootPositionCurves[1]);
                    animationClip.SetCurve("", typeof(Animator), "RootT.z", rootPositionCurves[2]);

                    animationClip.SetCurve("", typeof(Animator), "RootQ.x", rootRotationCurves[0]);
                    animationClip.SetCurve("", typeof(Animator), "RootQ.y", rootRotationCurves[1]);
                    animationClip.SetCurve("", typeof(Animator), "RootQ.z", rootRotationCurves[2]);
                    animationClip.SetCurve("", typeof(Animator), "RootQ.w", rootRotationCurves[3]);

                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        if (muscleCurves[i].keys.Length > 0)
                        {
                            var muscleName = GetCorrectMusclePropertyName(HumanTrait.MuscleName[i]);
                            animationClip.SetCurve("", typeof(Animator), muscleName, muscleCurves[i]);
                        }
                    }
                }

                if (blendShapeCurves.Count > 0 && settings.shapeRenderer != null)
                {
                    var rendererPath = AnimationUtility.CalculateTransformPath(settings.shapeRenderer.transform, animator.transform);

                    foreach (var kvp in blendShapeCurves)
                    {
                        if (kvp.Value.keys.Length > 0)
                        {
                            animationClip.SetCurve(rendererPath, typeof(SkinnedMeshRenderer), $"blendShape.{kvp.Key}", kvp.Value);
                        }
                    }
                }

                animationClip.name = System.IO.Path.GetFileNameWithoutExtension(settings.outputPath);

                // Ensure parent directory exists
                var directory = System.IO.Path.GetDirectoryName(settings.outputPath);
                if (!System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                }

                // Check if asset already exists to avoid recreating .meta files
                var existingClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(settings.outputPath);
                if (existingClip != null)
                {
                    // Overwrite existing asset content
                    EditorUtility.CopySerialized(animationClip, existingClip);
                    EditorUtility.SetDirty(existingClip);
                    AssetDatabase.SaveAssets();
                    return existingClip;
                }
                else
                {
                    // Create new asset
                    AssetDatabase.CreateAsset(animationClip, settings.outputPath);
                    AssetDatabase.SaveAssets();
                    return animationClip;
                }
            }
        }

        private static bool ValidateSettings(ConversionSettings settings)
        {
            if (settings.targetAvatar == null)
            {
                Debug.LogError("Target avatar is required");
                return false;
            }

            if (string.IsNullOrEmpty(settings.inputFilePath))
            {
                Debug.LogError("Input file path is required");
                return false;
            }

            if (!System.IO.File.Exists(settings.inputFilePath))
            {
                Debug.LogError($"Input file does not exist: {settings.inputFilePath}");
                return false;
            }

            if (string.IsNullOrEmpty(settings.outputPath))
            {
                Debug.LogError("Output path is required");
                return false;
            }

            if (settings.resolution.x != 80 || settings.resolution.y != 45)
            {
                Debug.LogWarning("Non-standard resolution detected. This may cause issues with motion decoding.");
            }

            if (settings.tileSize.x <= 0 || settings.tileSize.y <= 0 || settings.tileSize.z <= 0)
            {
                Debug.LogError("Invalid tile size parameters");
                return false;
            }

            if (settings.tileRadix <= 0)
            {
                Debug.LogError("Tile radix must be positive");
                return false;
            }

            return true;
        }

        private static string GetCorrectMusclePropertyName(string muscleName)
        {
            // Convert finger muscle names to correct animation property names
            // Example: "Left Index 1 Stretched" -> "LeftHand.Index.1 Stretched"

            if (muscleName.Contains("Left") && (muscleName.Contains("Thumb") || muscleName.Contains("Index") ||
                muscleName.Contains("Middle") || muscleName.Contains("Ring") || muscleName.Contains("Little")))
            {
                string corrected = muscleName.Replace("Left ", "LeftHand.");
                corrected = corrected.Replace(" ", ".");
                corrected = corrected.Replace(".Stretched", " Stretched");
                return corrected;
            }

            if (muscleName.Contains("Right") && (muscleName.Contains("Thumb") || muscleName.Contains("Index") ||
                muscleName.Contains("Middle") || muscleName.Contains("Ring") || muscleName.Contains("Little")))
            {
                string corrected = muscleName.Replace("Right ", "RightHand.");
                corrected = corrected.Replace(" ", ".");
                corrected = corrected.Replace(".Stretched", " Stretched");
                return corrected;
            }

            // Return original name for non-finger muscles
            return muscleName;
        }

        private static Texture2D CreateTextureFromFrameData(float[] frameData, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
            texture.SetPixelData(frameData, 0);
            texture.Apply();

            return texture;
        }

        private static void AddLinearKey(AnimationCurve curve, float time, float value)
        {
            var keyIndex = curve.AddKey(time, value);
            AnimationUtility.SetKeyLeftTangentMode(curve, keyIndex, AnimationUtility.TangentMode.Linear);
            AnimationUtility.SetKeyRightTangentMode(curve, keyIndex, AnimationUtility.TangentMode.Linear);
        }

        private static RenderTexture ProcessWithVideoDecoder(Texture2D inputTexture)
        {
            var motionDecMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/ShaderMotion/Material/MotionDec.mat");

            if (motionDecMat == null)
            {
                Debug.LogError("MotionDec material not found");
                return null;
            }

            var motionDecMatClone = Object.Instantiate<Material>(motionDecMat);

            var outputTexture = new RenderTexture(160, 90, 0, RenderTextureFormat.ARGBFloat);
            outputTexture.Create();

            motionDecMatClone.SetTexture("_MainTex", inputTexture);
            Graphics.Blit(inputTexture, outputTexture, motionDecMatClone);

            return outputTexture;
        }
    }
}
