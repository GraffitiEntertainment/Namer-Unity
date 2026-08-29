using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// The first-party processor window (<c>Tools &gt; NAMER &gt; Processor</c>) that
    /// presents the 03-01 pipeline to an artist: inspect the selection, preview the
    /// actual mesh before/after with a shared orbit/zoom camera, tweak the AO un-multiply
    /// strength with a 300 ms debounced GPU recompute, configure the output folder, and
    /// run the single shared "Process with NAMER" entry point (D-13).
    ///
    /// The interactive preview path calls only <see cref="NamerComputePipeline"/> and
    /// <see cref="NamerPreviewRenderer"/> — it never writes to disk (D-10). The sole disk
    /// writer remains <see cref="NamerProcessor"/>/AssetGenerator, reachable only from the
    /// Process button and the two context-menu command surfaces (UI-02).
    /// </summary>
    public sealed class NamerEditorWindow : EditorWindow
    {
        private static readonly string[] DebugChannelLabels =
        {
            "Shaded", "Base Color", "AO", "Normal", "Roughness", "Metallic", "Emissive",
        };

        private static readonly int SurfaceMapId = Shader.PropertyToID("_SurfaceMap");
        private static readonly int BaseResidualMapId = Shader.PropertyToID("_BaseResidualMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");

        private NamerProcessorSettings _settings;
        private NamerPreviewRenderer _preview;
        private NamerDebugChannelMaterial _debugMaterialFactory;
        private NamerComputePipeline _pipeline;

        private Material _namerMaterial;
        private Material _debugMaterial;
        private NamerAfterPanelState _afterPanelState = new NamerAfterPanelState();
        private Material _generatedMaterial;
        private Texture2D _generatedSurface;
        private Texture2D _generatedBase;
        private NamerComputeResult _liveResult;
        private RenderTexture _previewBaseRt;

        private UnityEngine.Object _selection;
        private NamerSourceModel _model;
        private Mesh _previewMesh;

        private float _aoUnmultiplyStrength = 1f;
        private float _aoBlurRadius;
        private float _aoStrength = 1f;
        private float _aoContrast = 1f;
        private Mesh _occluderMesh;
        private string _occluderWarning = string.Empty;
        private int _debugChannel;

        private bool _dirty;
        private double _lastChange;
        private bool _busy;
        private bool _recomputing;
        private ColorSpace _previewColorSpace = ColorSpace.Uninitialized;

        private string _status = string.Empty;
        private bool _statusIsError;

        private NamerMaterialInspection PrimaryInspection
        {
            get { return _model != null && _model.Materials.Count > 0 ? _model.Materials[0] : null; }
        }

        [MenuItem("Tools/NAMER/Processor")]
        public static void Open()
        {
            GetWindow<NamerEditorWindow>("NAMER Processor");
        }

        [MenuItem("Assets/Process with NAMER")]
        private static void ProcessSelectedAsset()
        {
            ProcessSelection(Selection.activeObject);
        }

        [MenuItem("GameObject/Process with NAMER")]
        private static void ProcessSelectedGameObject()
        {
            ProcessSelection(Selection.activeGameObject != null ? Selection.activeGameObject : Selection.activeObject);
        }

        private static void ProcessSelection(UnityEngine.Object selection)
        {
            NamerProcessResult result = NamerProcessor.Process(selection, new NamerProcessorSettings());
            LogResult(result);
        }

        private static void LogResult(NamerProcessResult result)
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.Log("[NAMER] Process with NAMER blocked: " + result.Error);
                return;
            }

            string message = "[NAMER] Process with NAMER: " + result.MaterialCount + " material(s) generated";
            if (result.Warnings.Count > 0)
            {
                message += " (" + result.Warnings.Count + " warning(s))";
            }

            Debug.Log(message);

            foreach (string warning in result.Warnings)
            {
                Debug.Log("[NAMER]   [warning] " + warning);
            }
        }

        private void OnEnable()
        {
            _settings = new NamerProcessorSettings();
            _preview = new NamerPreviewRenderer();
            _debugMaterialFactory = new NamerDebugChannelMaterial();

            EditorApplication.update += Tick;
            Selection.selectionChanged += OnSelectionChanged;

            _selection = ResolveSelection();
            RebuildInspection();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            Selection.selectionChanged -= OnSelectionChanged;

            ReleaseLiveResult();

            if (_pipeline != null)
            {
                _pipeline.Dispose();
                _pipeline = null;
            }

            if (_namerMaterial != null)
            {
                DestroyImmediate(_namerMaterial);
                _namerMaterial = null;
            }

            if (_debugMaterial != null)
            {
                DestroyImmediate(_debugMaterial);
                _debugMaterial = null;
            }

            if (_preview != null)
            {
                _preview.Dispose();
                _preview = null;
            }
        }

        private static UnityEngine.Object ResolveSelection()
        {
            return Selection.activeObject != null ? Selection.activeObject : Selection.activeGameObject;
        }

        private void OnSelectionChanged()
        {
            _selection = ResolveSelection();
            RebuildInspection();
        }

        private void RebuildInspection()
        {
            _model = SourceInspector.Inspect(_selection);

            _aoUnmultiplyStrength = _settings.AoUnmultiplyStrength;
            _aoBlurRadius = _settings.AoBlurRadius;
            _aoStrength = _settings.AoStrength;
            _aoContrast = _settings.AoContrast;

            _previewMesh = ResolvePreviewMesh(_selection);
            if (_previewMesh != null)
            {
                _preview.Frame(_previewMesh);
            }

            NamerMaterialInspection inspection = PrimaryInspection;
            if (inspection != null)
            {
                inspection.BakeSourceMesh = _previewMesh;
            }

            _occluderMesh = null;
            _occluderWarning = string.Empty;

            _debugChannel = 0;
            _status = string.Empty;
            _statusIsError = false;

            ResolveGeneratedPreview();

            MarkDirty();
        }

        /// <summary>
        /// Resolves whether generated assets exist for the current selection and, when they
        /// do, binds the generated surface/base textures to the debug material. Loaded
        /// generated materials/textures are persistent <c>AssetDatabase</c> assets and are
        /// never destroyed here (including in <c>OnDisable</c>).
        /// </summary>
        private void ResolveGeneratedPreview()
        {
            _afterPanelState.Reset();
            _afterPanelState.GeneratedAvailable = false;
            _generatedMaterial = null;
            _generatedSurface = null;
            _generatedBase = null;

            NamerMaterialInspection inspection = PrimaryInspection;
            if (inspection == null || _selection == null)
            {
                return;
            }

            string folder = AssetGenerator.ComposeDestinationFolder(_settings.Destination, _selection.name);
            _generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                AssetGenerator.ComposePath(inspection, _settings, folder, ".mat"));
            _generatedSurface = AssetDatabase.LoadAssetAtPath<Texture2D>(
                AssetGenerator.ComposePath(inspection, _settings, folder, "_Surface.png"));
            _generatedBase = AssetDatabase.LoadAssetAtPath<Texture2D>(
                AssetGenerator.ComposePath(inspection, _settings, folder, "_Base.png"));

            if (_generatedMaterial == null)
            {
                return;
            }

            _afterPanelState.GeneratedAvailable = true;

            if (_debugMaterial != null)
            {
                _debugMaterialFactory.SetTextures(_debugMaterial, _generatedSurface, _generatedBase);
            }
        }

        private void Tick()
        {
            // A live color-space flip re-gates both the upload path and the preview
            // base-map encode, but only RecomputePreview evaluates those gates, so a
            // flip must re-run the debounced recompute (else the window keeps rendering
            // textures bound under the old space).
            if (QualitySettings.activeColorSpace != _previewColorSpace)
            {
                _previewColorSpace = QualitySettings.activeColorSpace;
                MarkDirty();
            }

            if (_busy || !_dirty)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastChange < NamerEditorConstants.DebounceSeconds)
            {
                return;
            }

            _dirty = false;
            RecomputePreview();
        }

        private void RecomputePreview()
        {
            NamerMaterialInspection inspection = PrimaryInspection;
            if (inspection == null)
            {
                return;
            }

            _recomputing = true;
            try
            {
                EnsurePipeline();
                ReleaseLiveResult();
                EnsureMaterials();

                inspection.AoUnmultiplyStrength = _aoUnmultiplyStrength;
                inspection.AoBlurRadius = _aoBlurRadius;
                inspection.AoStrength = _aoStrength;
                inspection.AoContrast = _aoContrast;
                if (inspection.BakeSourceMesh == null)
                {
                    inspection.BakeSourceMesh = _previewMesh;
                }

                // D-06: an invalid high-res occluder (empty, or the same mesh as the bake
                // source) warns visibly and falls back to the selected mesh — never a hard
                // failure.
                if (_occluderMesh != null && (_occluderMesh.vertexCount == 0 || _occluderMesh == inspection.BakeSourceMesh))
                {
                    _occluderWarning = "High-res occluder is empty or the same as the bake source — falling back to the selected mesh.";
                    _occluderMesh = null;
                }
                else
                {
                    _occluderWarning = string.Empty;
                }

                inspection.OccluderMesh = _occluderMesh;

                _liveResult = _pipeline.Process(inspection);

                RenderTexture previewBaseMap = ResolvePreviewBaseMap();

                _namerMaterial.SetTexture(SurfaceMapId, _liveResult.PackedSurface);
                _namerMaterial.SetTexture(BaseResidualMapId, previewBaseMap);
                _namerMaterial.SetColor(BaseColorId, inspection.BaseColor);
                _namerMaterial.SetColor(EmissionColorId, inspection.EmissionColor);
                _namerMaterial.SetFloat(OcclusionStrengthId, inspection.OcclusionStrength);

                if (inspection.Emissive > 0f)
                {
                    _namerMaterial.EnableKeyword("_EMISSION");
                }
                else
                {
                    _namerMaterial.DisableKeyword("_EMISSION");
                }

                if (_afterPanelState.PreferGenerated && _generatedSurface != null && _generatedBase != null)
                {
                    _debugMaterialFactory.SetTextures(_debugMaterial, _generatedSurface, _generatedBase);
                }
                else
                {
                    _debugMaterialFactory.SetTextures(_debugMaterial, _liveResult.PackedSurface, previewBaseMap);
                }
                _debugMaterialFactory.SetChannel(_debugMaterial, Mathf.Max(0, _debugChannel - 1));
                _debugMaterial.SetFloat(OcclusionStrengthId, inspection.OcclusionStrength);

                _status = string.Empty;
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                _status = "Processing failed: " + ex.Message;
                _statusIsError = true;
            }
            finally
            {
                _recomputing = false;
            }

            TriggerAutomaticBake(inspection);

            Repaint();
        }

        /// <summary>
        /// Automatic geometry-bake trigger (D-07): after a successful recompute, when the
        /// source has no authored <c>_OcclusionMap</c>, a bake-capable mesh, and no cached
        /// bake, prime the bake via the synchronous, idempotent <see cref="NamerComputePipeline.RequestBake"/>
        /// and re-dirty the preview so the next recompute routes the cached bake through the
        /// 03.1-02 three-way gate. The bake runs off the debounce tick (after the recompute
        /// completes) and is cached once per mesh/occluder/resolution.
        /// </summary>
        private void TriggerAutomaticBake(NamerMaterialInspection inspection)
        {
            if (inspection == null || _liveResult == null || inspection.OcclusionMap != null)
            {
                return;
            }

            if (inspection.BakeSourceMesh == null)
            {
                return;
            }

            if (_pipeline.HasCachedBake(inspection, _liveResult.Width, _liveResult.Height))
            {
                return;
            }

            bool completed = _pipeline.RequestBake(inspection, _liveResult.Width, _liveResult.Height, () => MarkDirty());
            if (!completed)
            {
                // Cancelled: leave the extraction result on screen and tell the user. No
                // re-dirty — the bake is intentionally not retried until the next change.
                _status = "AO bake cancelled — showing image-space extraction.";
                _statusIsError = false;
            }
        }

        private void EnsurePipeline()
        {
            if (_pipeline == null)
            {
                _pipeline = new NamerComputePipeline();
            }
        }

        private void EnsureMaterials()
        {
            if (_namerMaterial == null)
            {
                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "Shader 'GraffitiEntertainment.Namer/NAMER' was not found. Ensure the NAMER shader compiled and imported.");
                }

                _namerMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (_debugMaterial == null)
            {
                _debugMaterial = _debugMaterialFactory.Create();
                _debugMaterial.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        /// <summary>
        /// Resolves the base color map the preview materials should sample. The pipeline's
        /// <see cref="NamerComputeResult.NormalizedBaseColor"/> is a linear float16 target,
        /// while <c>_BaseResidualMap</c> is an sRGB-declared slot: a Linear project
        /// hardware-decodes the saved sRGB PNG on sample, so binding the raw linear target
        /// is already correct there. A Gamma project performs no decode anywhere, so the
        /// raw linear values would shade darker than the before pane (and darker than the
        /// correctly saved material). Re-encode through the raw-copy material with
        /// <c>_REENCODE_SRGB</c> — the same exact IEC 61966-2-1 primitive the save path
        /// uses — so the preview matches the saved material in both color spaces.
        /// </summary>
        private RenderTexture ResolvePreviewBaseMap()
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Gamma)
            {
                return _liveResult.NormalizedBaseColor;
            }

            if (_previewBaseRt == null
                || _previewBaseRt.width != _liveResult.Width
                || _previewBaseRt.height != _liveResult.Height)
            {
                if (_previewBaseRt != null)
                {
                    _previewBaseRt.Release();
                    DestroyImmediate(_previewBaseRt);
                }

                var descriptor = new RenderTextureDescriptor(
                    _liveResult.Width, _liveResult.Height, GraphicsFormat.R8G8B8A8_UNorm, 0)
                {
                    sRGB = false,
                };
                _previewBaseRt = new RenderTexture(descriptor);
                _previewBaseRt.Create();
            }

            Material rawCopy = NamerComputePipeline.RawCopyMaterial();
            rawCopy.EnableKeyword(NamerComputePipeline.ReencodeSrgbKeyword);
            Graphics.Blit(_liveResult.NormalizedBaseColor, _previewBaseRt, rawCopy);
            rawCopy.DisableKeyword(NamerComputePipeline.ReencodeSrgbKeyword);
            return _previewBaseRt;
        }

        private void ReleaseLiveResult()
        {
            if (_previewBaseRt != null)
            {
                _previewBaseRt.Release();
                DestroyImmediate(_previewBaseRt);
                _previewBaseRt = null;
            }

            if (_liveResult == null)
            {
                return;
            }

            if (_pipeline != null)
            {
                _pipeline.ReleaseResult(_liveResult);
            }

            _liveResult = null;
        }

        private void MarkDirty()
        {
            _dirty = true;
            _lastChange = EditorApplication.timeSinceStartup;
        }

        private void OnGUI()
        {
            NamerMaterialInspection inspection = PrimaryInspection;

            DrawSourceSection();
            DrawPreviewSection(inspection);
            DrawProcessingSection(inspection);
            DrawOutputSection();
            DrawActionSection(inspection);
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            if (_selection == null)
            {
                EditorGUILayout.LabelField("No source selected", EditorStyles.largeLabel);
                EditorGUILayout.LabelField(
                    "Select a GameObject, prefab, model, material, or folder in the Project or Hierarchy window, then press Process with NAMER.",
                    EditorStyles.label);
                EditorGUILayout.Space();
                return;
            }

            EditorGUILayout.LabelField(_selection.name + " (" + _selection.GetType().Name + ")", EditorStyles.label);

            if (_model != null && _model.Materials.Count == 0)
            {
                EditorGUILayout.LabelField("No materials found", EditorStyles.largeLabel);
                EditorGUILayout.LabelField(
                    "The selection contains no inspectable materials. Select a GameObject, prefab, model, material, or folder with PBR materials.",
                    EditorStyles.label);
            }
            else if (_model != null)
            {
                foreach (NamerMaterialInspection material in _model.Materials)
                {
                    string name = material.Material != null ? material.Material.name : "(null)";
                    EditorGUILayout.LabelField(name + " — " + material.ShaderName, EditorStyles.label);
                }
            }

            if (_model != null)
            {
                foreach (string warning in _model.Warnings)
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawPreviewSection(NamerMaterialInspection inspection)
        {
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

            if (_previewMesh == null || inspection == null)
            {
                EditorGUILayout.HelpBox(
                    "No mesh to preview. Select a GameObject or model to see a before/after preview.",
                    MessageType.Info);
                EditorGUILayout.Space();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Before", EditorStyles.largeLabel);
            GUILayout.Label("After", EditorStyles.largeLabel);
            EditorGUILayout.EndHorizontal();

            float previewWidth = Mathf.Max(EditorGUIUtility.currentViewWidth, 256f);
            Rect previewRect = GUILayoutUtility.GetRect(previewWidth, previewWidth * 0.5f);
            HandlePreviewCameraInput(previewRect);

            Material beforeMaterial = inspection.Material;
            Material afterMaterial;
            if (_debugChannel == 0)
            {
                afterMaterial = (_afterPanelState.PreferGenerated && _generatedMaterial != null)
                    ? _generatedMaterial
                    : _namerMaterial;
            }
            else
            {
                afterMaterial = _debugMaterial;
            }

            if (afterMaterial == null)
            {
                afterMaterial = beforeMaterial;
            }

            if (beforeMaterial == null)
            {
                GUI.Box(previewRect, GUIContent.none);
                return;
            }

            Texture previewTexture = _preview.Render(_previewMesh, beforeMaterial, afterMaterial, previewRect);
            if (previewTexture != null)
            {
                GUI.DrawTexture(previewRect, previewTexture, ScaleMode.StretchToFill);
            }
            else
            {
                GUI.Box(previewRect, GUIContent.none);
            }

            EditorGUI.BeginDisabledGroup(_busy);
            int newChannel = GUILayout.Toolbar(_debugChannel, DebugChannelLabels);
            if (newChannel != _debugChannel)
            {
                _debugChannel = newChannel;
                if (_debugMaterial != null)
                {
                    _debugMaterialFactory.SetChannel(_debugMaterial, Mathf.Max(0, _debugChannel - 1));
                }

                Repaint();
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
        }

        private void HandlePreviewCameraInput(Rect previewRect)
        {
            Event current = Event.current;
            if (current == null || !previewRect.Contains(current.mousePosition))
            {
                return;
            }

            if (current.type == EventType.MouseDrag)
            {
                _preview.Orbit(current.delta.x, current.delta.y);
                current.Use();
                Repaint();
            }
            else if (current.type == EventType.ScrollWheel)
            {
                _preview.Zoom(current.delta.y);
                current.Use();
                Repaint();
            }
        }

        private void DrawProcessingSection(NamerMaterialInspection inspection)
        {
            EditorGUILayout.LabelField("Processing", EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(inspection == null || _busy);

            float newAo = EditorGUILayout.Slider(
                new GUIContent(
                    "AO Un-multiply Strength",
                    "Automatically recomputes the preview in memory " + NamerEditorConstants.DebounceSeconds
                        + " s after the slider stops — nothing is written to disk."),
                _aoUnmultiplyStrength, 0f, 1f);
            if (!Mathf.Approximately(newAo, _aoUnmultiplyStrength))
            {
                _aoUnmultiplyStrength = newAo;
                _settings.AoUnmultiplyStrength = newAo;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            float newAoBlur = EditorGUILayout.Slider(
                new GUIContent(
                    "AO Blur Radius",
                    "Blurs the AO output in texels (0 = off). Automatically recomputes the preview in memory "
                        + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."),
                _aoBlurRadius, 0f, 16f);
            if (!Mathf.Approximately(newAoBlur, _aoBlurRadius))
            {
                _aoBlurRadius = newAoBlur;
                _settings.AoBlurRadius = newAoBlur;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            float newAoStrength = EditorGUILayout.Slider(
                new GUIContent(
                    "AO Strength",
                    "AO strength (1 = full AO, 0 = white/no AO). Automatically recomputes the preview in memory "
                        + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."),
                _aoStrength, 0f, 1f);
            if (!Mathf.Approximately(newAoStrength, _aoStrength))
            {
                _aoStrength = newAoStrength;
                _settings.AoStrength = newAoStrength;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            float newAoContrast = EditorGUILayout.Slider(
                new GUIContent(
                    "AO Contrast",
                    "AO contrast (1 = identity, pivot 0.5). Automatically recomputes the preview in memory "
                        + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."),
                _aoContrast, 0f, 4f);
            if (!Mathf.Approximately(newAoContrast, _aoContrast))
            {
                _aoContrast = newAoContrast;
                _settings.AoContrast = newAoContrast;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            Mesh newOccluder = EditorGUILayout.ObjectField(
                new GUIContent(
                    "High-res Occluder",
                    "Optional high-res mesh used as the geometry-bake occluder; empty/invalid falls back to the selected mesh. Automatically recomputes the preview in memory "
                        + NamerEditorConstants.DebounceSeconds + " s after the assignment — nothing is written to disk."),
                _occluderMesh, typeof(Mesh), false) as Mesh;
            if (newOccluder != _occluderMesh)
            {
                _occluderMesh = newOccluder;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_occluderWarning))
            {
                EditorGUILayout.HelpBox(_occluderWarning, MessageType.Warning);
            }

            // Visible feedback for the otherwise-invisible debounced preview recompute:
            // pending/recomputing while dirty, settled once the compute finishes.
            EditorGUILayout.LabelField(
                _dirty || _recomputing ? "Recomputing preview…" : "Preview up to date",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            EditorGUI.BeginDisabledGroup(_busy);

            string destination = EditorGUILayout.TextField("Destination", _settings.Destination);
            if (!string.Equals(destination, _settings.Destination, StringComparison.Ordinal))
            {
                _settings.Destination = destination;
            }

            string prefix = EditorGUILayout.TextField("Prefix", _settings.Prefix);
            if (!string.Equals(prefix, _settings.Prefix, StringComparison.Ordinal))
            {
                _settings.Prefix = prefix;
            }

            string suffix = EditorGUILayout.TextField("Suffix", _settings.Suffix);
            if (!string.Equals(suffix, _settings.Suffix, StringComparison.Ordinal))
            {
                _settings.Suffix = suffix;
            }

            bool overwrite = EditorGUILayout.Toggle("Overwrite generated", _settings.OverwriteGenerated);
            if (overwrite != _settings.OverwriteGenerated)
            {
                _settings.OverwriteGenerated = overwrite;
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space();
        }

        private void DrawActionSection(NamerMaterialInspection inspection)
        {
            EditorGUILayout.LabelField("Action", EditorStyles.boldLabel);

            bool canProcess = inspection != null && !_busy && !_recomputing;

            EditorGUI.BeginDisabledGroup(!canProcess);
            string buttonLabel = _busy ? "Processing…" : "Process with NAMER";
            if (GUILayout.Button(buttonLabel))
            {
                RunProcess();
            }

            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.HelpBox(_status, _statusIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void RunProcess()
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
            _status = string.Empty;
            _statusIsError = false;
            Repaint();

            try
            {
                NamerProcessResult result = NamerProcessor.Process(_selection, _settings);

                foreach (string warning in result.Warnings)
                {
                    Debug.Log("[NAMER]   [warning] " + warning);
                }

                _status = DescribeResult(result);
                _statusIsError = !string.IsNullOrEmpty(result.Error);
                if (string.IsNullOrEmpty(result.Error))
                {
                    ResolveGeneratedPreview();
                }
            }
            catch (Exception ex)
            {
                _status = "Processing failed: " + ex.Message;
                _statusIsError = true;
            }
            finally
            {
                _busy = false;
                Repaint();
            }
        }

        private string DescribeResult(NamerProcessResult result)
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                bool blocked = result.Error.IndexOf("Refusing to overwrite", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Error.IndexOf("Destination", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Error.IndexOf("selection", StringComparison.OrdinalIgnoreCase) >= 0
                    || result.Error.IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0;
                return blocked ? "Process blocked: " + result.Error : "Processing failed: " + result.Error;
            }

            return "Generated " + result.MaterialCount + " material(s) under " + _settings.Destination + ".";
        }

        private static Mesh ResolvePreviewMesh(UnityEngine.Object selection)
        {
            if (selection == null)
            {
                return null;
            }

            if (selection is GameObject gameObject)
            {
                string assetPath = AssetDatabase.GetAssetPath(gameObject);

                // Scene object: read renderer meshes directly.
                if (string.IsNullOrEmpty(assetPath))
                {
                    return FindMeshInGameObject(gameObject);
                }

                PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(gameObject);
                if (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant)
                {
                    return FindMeshInPrefab(assetPath);
                }

                return FindMeshSubAsset(assetPath);
            }

            string path = AssetDatabase.GetAssetPath(selection);
            return string.IsNullOrEmpty(path) ? null : FindMeshSubAsset(path);
        }

        private static Mesh FindMeshInGameObject(GameObject gameObject)
        {
            MeshFilter filter = gameObject.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && filter.sharedMesh != null)
            {
                return filter.sharedMesh;
            }

            SkinnedMeshRenderer skinned = gameObject.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (skinned != null && skinned.sharedMesh != null)
            {
                return skinned.sharedMesh;
            }

            return null;
        }

        private static Mesh FindMeshInPrefab(string assetPath)
        {
            // Embedded meshes are persistent sub-assets of the prefab; prefer them so the
            // returned mesh is never owned by the temporary prefab contents (WR-06).
            Mesh subAsset = FindMeshSubAsset(assetPath);
            if (subAsset != null)
            {
                return subAsset;
            }

            // External (FBX) meshes are not part of the prefab asset; resolve through the
            // prefab contents but only keep a mesh that is a persistent asset — otherwise
            // UnloadPrefabContents would destroy it.
            GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                Mesh mesh = contents != null ? FindMeshInGameObject(contents) : null;
                return mesh != null && AssetDatabase.Contains(mesh) ? mesh : null;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static Mesh FindMeshSubAsset(string assetPath)
        {
            foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (subAsset is Mesh mesh)
                {
                    return mesh;
                }
            }

            return null;
        }
    }
}
