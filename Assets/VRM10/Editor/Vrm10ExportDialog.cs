﻿using System;
using System.Collections.Generic;
using System.IO;
using UniGLTF;
using UniGLTF.M17N;
using UniGLTF.MeshUtility;
using UnityEditor;
using UnityEngine;
using VrmLib;
using VRMShaders;

namespace UniVRM10
{
    public class VRM10ExportDialog : ExportDialogBase
    {
        const string CONVERT_HUMANOID_KEY = VRMVersion.MENU + "/Export VRM-1.0";

        [MenuItem(CONVERT_HUMANOID_KEY, false, 0)]
        private static void ExportFromMenu()
        {
            var window = (VRM10ExportDialog)GetWindow(typeof(VRM10ExportDialog));
            window.titleContent = new GUIContent("VRM-1.0 Exporter");
            window.Show();
        }


        enum Tabs
        {
            Meta,
            Mesh,
            ExportSettings,
        }
        Tabs _tab;


        VRM10ExportSettings m_settings;
        Editor m_settingsInspector;


        MeshExportValidator m_meshes;
        Editor m_meshesInspector;

        VRM10Object m_meta;
        VRM10Object Vrm
        {
            get { return m_meta; }
            set
            {
                if (value != null && AssetDatabase.IsSubAsset(value))
                {
                    // SubAsset is readonly. copy
                    Debug.Log("copy VRM10ObjectMeta");
                    value.Meta.CopyTo(m_tmpObject.Meta);
                    return;
                }

                if (m_meta == value)
                {
                    return;
                }
                m_metaEditor = default;
                m_meta = value;
            }
        }
        VRM10Object m_tmpObject;
        VRM10MetaEditor m_metaEditor;

        protected override void Initialize()
        {
            m_tmpObject = ScriptableObject.CreateInstance<VRM10Object>();
            m_tmpObject.name = "_vrm1_";
            m_tmpObject.Meta.Authors = new List<string> { "" };

            m_settings = ScriptableObject.CreateInstance<VRM10ExportSettings>();
            m_settingsInspector = Editor.CreateEditor(m_settings);

            m_meshes = ScriptableObject.CreateInstance<MeshExportValidator>();
            m_meshesInspector = Editor.CreateEditor(m_meshes);

            State.ExportRootChanged += (root) =>
            {
                // update meta
                if (root == null)
                {
                    Vrm = null;
                }
                else
                {
                    var controller = root.GetComponent<Vrm10Instance>();
                    if (controller != null)
                    {
                        Vrm = controller.Vrm;
                    }
                    else
                    {
                        Vrm = null;
                    }

                    // default setting
                    // m_settings.PoseFreeze =
                    // MeshUtility.Validators.HumanoidValidator.HasRotationOrScale(root)
                    // || m_meshes.Meshes.Any(x => x.ExportBlendShapeCount > 0 && !x.HasSkinning)
                    // ;
                }
            };
        }

        protected override void Clear()
        {
            // m_settingsInspector
            UnityEditor.Editor.DestroyImmediate(m_settingsInspector);
            m_settingsInspector = null;
            // m_meshesInspector
            UnityEditor.Editor.DestroyImmediate(m_meshesInspector);
            m_meshesInspector = null;
            // Meta
            Vrm = null;
            ScriptableObject.DestroyImmediate(m_tmpObject);
            m_tmpObject = null;
            // m_settings
            ScriptableObject.DestroyImmediate(m_settings);
            m_settings = null;
            // m_meshes
            ScriptableObject.DestroyImmediate(m_meshes);
            m_meshes = null;
        }

        protected override IEnumerable<Validator> ValidatorFactory()
        {
            HumanoidValidator.MeshInformations = m_meshes.Meshes;
            // HumanoidValidator.EnableFreeze = m_settings.PoseFreeze;

            yield return HierarchyValidator.Validate;
            if (!State.ExportRoot)
            {
                yield break;
            }

            yield return HumanoidValidator.Validate;

            // MeshUtility.Validators.HumanoidValidator.EnableFreeze = false;
            // yield return MeshUtility.Validators.HumanoidValidator.Validate;

            // yield return VRMExporterValidator.Validate;
            // yield return VRMSpringBoneValidator.Validate;

            // var firstPerson = State.ExportRoot.GetComponent<VRMFirstPerson>();
            // if (firstPerson != null)
            // {
            //     yield return firstPerson.Validate;
            // }

            // var proxy = State.ExportRoot.GetComponent<VRMBlendShapeProxy>();
            // if (proxy != null)
            // {
            //     yield return proxy.Validate;
            // }

            var vrm = Vrm ? Vrm : m_tmpObject;
            yield return vrm.Meta.Validate;
        }

        protected override void OnLayout()
        {
            // m_settings, m_meshes.Meshes
            m_meshes.SetRoot(State.ExportRoot, m_settings.MeshExportSettings, new DefualtBlendShapeExportFilter());
        }

        protected override bool DoGUI(bool isValid)
        {
            if (State.ExportRoot == null)
            {
                return false;
            }

            if (State.ExportRoot.GetComponent<Animator>() != null)
            {
                //
                // T-Pose
                //
                // if (GUILayout.Button("T-Pose"))
                // {
                //     if (State.ExportRoot != null)
                //     {
                //         // fallback
                //         Undo.RecordObjects(State.ExportRoot.GetComponentsInChildren<Transform>(), "tpose");
                //         VRMBoneNormalizer.EnforceTPose(State.ExportRoot);
                //     }
                // }

                var backup = GUI.enabled;
                GUI.enabled = State.ExportRoot.scene.IsValid();
                if (GUI.enabled)
                {
                    EditorGUILayout.HelpBox(EnableTPose.ENALBE_TPOSE_BUTTON.Msg(), MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(EnableTPose.DISABLE_TPOSE_BUTTON.Msg(), MessageType.Warning);
                }

                if (GUILayout.Button("T-Pose" + "(unity internal)"))
                {
                    if (State.ExportRoot != null)
                    {
                        Undo.RecordObjects(State.ExportRoot.GetComponentsInChildren<Transform>(), "tpose.internal");
                        if (InternalTPose.TryMakePoseValid(State.ExportRoot))
                        {
                            // done
                            Repaint();
                        }
                        else
                        {
                            Debug.LogWarning("not found");
                        }
                    }
                }

                GUI.enabled = backup;
            }

            if (!isValid)
            {
                return false;
            }

            if (m_tmpObject == null)
            {
                // disabled
                return false;
            }

            // tabbar
            _tab = TabBar.OnGUI(_tab);
            switch (_tab)
            {
                case Tabs.Meta:
                    if (m_metaEditor == null)
                    {
                        SerializedObject so;
                        if (m_meta != null)
                        {
                            so = new SerializedObject(Vrm);
                        }
                        else
                        {
                            so = new SerializedObject(m_tmpObject);
                        }
                        m_metaEditor = VRM10MetaEditor.Create(so);
                    }
                    m_metaEditor.OnInspectorGUI();
                    break;

                case Tabs.Mesh:
                    m_meshesInspector.OnInspectorGUI();
                    break;

                case Tabs.ExportSettings:
                    m_settingsInspector.OnInspectorGUI();
                    break;
            }

            return true;
        }

        protected override string SaveTitle => "Vrm1";
        protected override string SaveName => $"{State.ExportRoot.name}.vrm";
        protected override string[] SaveExtensions => new string[] { "vrm" };

        string m_logLabel;

        protected override void ExportPath(string path)
        {
            m_logLabel = "";

            m_logLabel += $"export...\n";

            var root = State.ExportRoot;

            try
            {
                var converter = new UniVRM10.ModelExporter();
                var model = converter.Export(root);

                if (HumanoidValidator.HasRotationOrScale(root))
                {
                    // 正規化
                    m_logLabel += $"normalize...\n";
                    var modifier = new ModelModifier(model);
                    modifier.SkinningBake();
                }

                // 右手系に変換
                m_logLabel += $"convert to right handed coordinate...\n";
                model.ConvertCoordinate(VrmLib.Coordinates.Vrm1, ignoreVrm: false);

                // export vrm-1.0
                var exporter = new UniVRM10.Vrm10Exporter(new EditorTextureSerializer(), new GltfExportSettings());
                var option = new VrmLib.ExportArgs();
                exporter.Export(root, model, converter, option, Vrm ? Vrm.Meta : m_tmpObject.Meta);

                var exportedBytes = exporter.Storage.ToBytes();

                m_logLabel += $"write to {path}...\n";
                File.WriteAllBytes(path, exportedBytes);
                Debug.Log("exportedBytes: " + exportedBytes.Length);

                var assetPath = UniGLTF.UnityPath.FromFullpath(path);
                if (assetPath.IsUnderAssetsFolder)
                {
                    // asset folder 内。import を発動
                    assetPath.ImportAsset();
                }
            }
            catch (Exception ex)
            {
                m_logLabel += ex.ToString();
                // rethrow
                throw;
            }
        }
    }
}
