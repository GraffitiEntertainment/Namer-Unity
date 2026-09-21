using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// The first-party processor window (<c>Tools &gt; NAMER &gt; Processor</c>) that
    /// presents the 03-01 pipeline to an artist: inspect the selection, preview the
    /// actual mesh before/after side by side through one shared orthographic camera with
    /// synced yaw+pitch rotation (one shared rotation for both panes) and orthographic-size
    /// zoom, tweak the AO un-multiply strength with a 300 ms debounced GPU recompute,
    /// configure the output folder, and run the single shared "Process with NAMER" entry
    /// point (D-13).
    ///
    /// The interactive preview path calls only <see cref="NamerComputePipeline"/> and
    /// <see cref="NamerPreviewRenderer"/> — it never writes to disk (D-10). The sole disk
    /// writer remains <see cref="NamerProcessor"/>/AssetGenerator, reachable only from the
    /// Process button and the two context-menu command surfaces (UI-02).
    /// </summary>
    public sealed class NamerEditorWindow : EditorWindow
    {
        /// <summary>
        /// Shaded-view input toggle labels (DIP-02): the seven neutral-default debug gates
        /// exposed as checkboxes in the Preview/Debug section. Read via reflection by
        /// <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal static readonly string[] ShaderInputToggleLabels =
        {
            "Base/Residual", "Roughness", "AO", "Metallic", "Emissive", "Vertex Color", "Normal",
        };

        /// <summary>
        /// Channel pane labels (D-12): the six per-channel views of the After material's
        /// packed textures, each decoded through the shared NAMER decode (no drift).
        /// Read via reflection by <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal static readonly string[] ChannelPaneLabels =
        {
            "Base", "Roughness", "AO", "Metallic", "Emissive", "Normal",
        };

        /// <summary>
        /// Decomposition statistics row labels (D-09 / VCOL-04), relabeled in 04.2 to
        /// removed-detail semantics: Coverage / Avg Error / Max Error / Removed-detail max
        /// error / Residual. Read via reflection by <c>NamerUIControlsTests</c>; the Residual
        /// row value is dynamic — see <see cref="ResidualNotWrittenLabel"/>.
        /// </summary>
        internal static readonly string[] DecompStatLabels =
        {
            "Coverage",
            "Avg Error",
            "Max Error",
            "Removed-detail max error",
            "Residual",
        };

        /// <summary>Residual-row value when no residual is written (04.2 one-texture outcome).</summary>
        internal const string ResidualNotWrittenLabel = "not written (one-texture)";

        private static readonly int SurfaceMapId = Shader.PropertyToID("_SurfaceMap");
        private static readonly int BaseResidualMapId = Shader.PropertyToID("_BaseResidualMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");
        private static readonly int DbgEnableResidualId = Shader.PropertyToID("_DbgEnableResidual");
        private static readonly int DbgEnableRoughnessId = Shader.PropertyToID("_DbgEnableRoughness");
        private static readonly int DbgEnableAoId = Shader.PropertyToID("_DbgEnableAO");
        private static readonly int DbgEnableMetallicId = Shader.PropertyToID("_DbgEnableMetallic");
        private static readonly int DbgEnableEmissiveId = Shader.PropertyToID("_DbgEnableEmissive");
        private static readonly int DbgEnableVertexColorId = Shader.PropertyToID("_DbgEnableVertexColor");
        private static readonly int DbgEnableNormalId = Shader.PropertyToID("_DbgEnableNormal");
        private static readonly int DbgRoughnessNeutralId = Shader.PropertyToID("_DbgRoughnessNeutral");
        private static readonly int ChannelId = Shader.PropertyToID("_Channel");
        private static readonly int WireColorId = Shader.PropertyToID("_WireColor");
        private static readonly Color TriangleWireframeColor = new Color(0f, 1f, 1f, 1f);

        private const float ChannelPaneSize = 48f;
        private const float ChannelPopupSize = 384f;

        private NamerProcessorSettings _settings;
        private NamerPreviewRenderer _preview;
        private NamerComputePipeline _pipeline;
        private NamerDecompPipeline _decompPipeline;
        private NamerDecompOutput _decompOutput;
        private NamerProjectionContext _projectionContext;
        private Mesh _previewSplitMesh;
        private NamerDecompErrorStats _decompStats;

        private Material _namerMaterial;
        private NamerAfterPanelState _afterPanelState = new NamerAfterPanelState();
        private Material _generatedMaterial;
        private Mesh _generatedMesh;
        private NamerComputeResult _liveResult;
        private RenderTexture _previewBaseRt;
        private Material _channelViewMaterial;
        private Material _wireMaterial;
        private Mesh _previewWireSource;
        private Mesh _previewWireMesh;

        private UnityEngine.Object _selection;
        private NamerSourceModel _model;
        private Mesh _previewMesh;
        // CR-01 mirror: why Process would SKIP decomposition for the current selection
        // (multi-material/multi-mesh), or null when decomposition may run. The preview
        // must not promise an extraction Process will not deliver (04.1 UAT regression:
        // the fit-driven preview showed extraction while Process fell back to Phase-3).
        private string _decompGuardReason;

        private float _aoUnmultiplyStrength = 1f;
        private float _aoBlurRadius;
        private float _aoStrength = 1f;
        private float _aoContrast = 1f;
        private bool _decompositionEnabled;
        private bool _roughnessStageEnabled;
        private bool _aoStageEnabled;
        private bool _metallicContributionEnabled;
        private bool _emissiveContributionEnabled;
        private bool _dbgResidualEnabled = true;
        private bool _dbgRoughnessEnabled = true;
        private bool _dbgAoEnabled = true;
        private bool _dbgMetallicEnabled = true;
        private bool _dbgEmissiveEnabled = true;
        private bool _dbgVertexColorEnabled = true;
        private bool _dbgNormalEnabled = true;
        private bool _showTriangles;
        private float _errorThreshold = NamerEditorConstants.DefaultErrorThreshold;
        private int _residualResolution;
        private float _roughnessExtractStrength = NamerEditorConstants.DefaultRoughnessExtractStrength;
        private NamerDipSource _dipSource;
        private bool _writeResidual;
        private Vector2 _scrollPosition;

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
            ReleaseDecompPreview();

            if (_decompPipeline != null)
            {
                _decompPipeline.Dispose();
                _decompPipeline = null;
            }

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

            if (_channelViewMaterial != null)
            {
                DestroyImmediate(_channelViewMaterial);
                _channelViewMaterial = null;
            }

            if (_wireMaterial != null)
            {
                DestroyImmediate(_wireMaterial);
                _wireMaterial = null;
            }

            if (_previewWireMesh != null)
            {
                DestroyImmediate(_previewWireMesh);
                _previewWireMesh = null;
                _previewWireSource = null;
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
            _decompositionEnabled = _settings.DecompositionEnabled;
            _roughnessStageEnabled = _settings.RoughnessStageEnabled;
            _aoStageEnabled = _settings.AoStageEnabled;
            _metallicContributionEnabled = _settings.MetallicContributionEnabled;
            _emissiveContributionEnabled = _settings.EmissiveContributionEnabled;
            _errorThreshold = _settings.ErrorThreshold;
            _residualResolution = _settings.ResidualResolution;
            _roughnessExtractStrength = _settings.RoughnessExtractStrength;
            _dipSource = (NamerDipSource)_settings.DipSource;
            _writeResidual = _settings.WriteResidual;

            _previewMesh = ResolvePreviewMesh(_selection);
            _decompGuardReason = NamerProcessor.DecompositionSkipReason(_selection, _model, _previewMesh);
            if (_previewMesh != null)
            {
                _preview.Frame(_previewMesh);
            }

            _status = string.Empty;
            _statusIsError = false;

            ResolveGeneratedPreview();

            MarkDirty();
        }

        /// <summary>
        /// Resolves whether generated assets exist for the current selection and, when they
        /// do, binds the generated material/mesh for the After pane and applies the current
        /// shaded-view debug gates. Loaded generated materials/meshes are persistent
        /// <c>AssetDatabase</c> assets and are never destroyed here (including in
        /// <c>OnDisable</c>).
        /// </summary>
        private void ResolveGeneratedPreview()
        {
            _afterPanelState.Reset();
            _afterPanelState.GeneratedAvailable = false;
            _generatedMaterial = null;
            _generatedMesh = null;

            NamerMaterialInspection inspection = PrimaryInspection;
            if (inspection == null || _selection == null)
            {
                return;
            }

            string folder = AssetGenerator.ComposeDestinationFolder(_settings.Destination, _selection.name);
            _generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                AssetGenerator.ComposePath(inspection, _settings, folder, ".mat"));
            _generatedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(
                AssetGenerator.ComposePath(inspection, _settings, folder, ".asset"));

            if (_generatedMaterial == null)
            {
                return;
            }

            _afterPanelState.GeneratedAvailable = true;
            ApplyDebugGates();
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
                ReleaseDecompPreview();
                EnsureMaterials();

                inspection.AoUnmultiplyStrength = _aoStageEnabled ? _aoUnmultiplyStrength : 0f;
                inspection.AoBlurRadius = _aoBlurRadius;
                inspection.AoStrength = _aoStrength;
                inspection.AoContrast = _aoContrast;
                inspection.RoughnessExtractStrength = _roughnessStageEnabled ? _roughnessExtractStrength : 0f;
                inspection.DipSource = _dipSource;

                // CR-01 mirror: when Process would skip decomposition for this selection
                // (multi-material/multi-mesh), the preview must show the non-decomposed
                // Phase-3 result Process actually generates — not a projection Process
                // will never deliver.
                bool decompWillRun = _decompositionEnabled && string.IsNullOrEmpty(_decompGuardReason);

                inspection.BakeSourceMesh = decompWillRun ? _previewMesh : null;
                int baseW = inspection.BaseMap != null ? inspection.BaseMap.width : NamerComputePipeline.DefaultBaseResolution;
                int baseH = inspection.BaseMap != null ? inspection.BaseMap.height : NamerComputePipeline.DefaultBaseResolution;

                // 04.2 projection preview wiring: when decomposition will run AND the dip
                // source is Removed Detail, build the projection context exactly like
                // NamerProcessor (its internal CreateProjectionContext) so the preview shows
                // the projection + transfer Process actually generates. Otherwise pass null
                // (Sobel fallback, or Phase-3 when decomposition is off / guard-skipped).
                _projectionContext = null;
                if (decompWillRun && _previewMesh != null && _dipSource == NamerDipSource.RemovedDetail)
                {
                    EnsureDecompPipeline();
                    NamerSplitResult fitSplit = MeshVertexSplitter.Split(_previewMesh);
                    _projectionContext = NamerProcessor.CreateProjectionContext(
                        fitSplit, _decompPipeline, baseW, baseH, _errorThreshold, _residualResolution, _writeResidual);
                }

                _liveResult = _pipeline.Process(inspection, _projectionContext);

                RenderTexture previewBaseMap = ResolvePreviewBaseMap();

                // D-08/D-12: run the in-memory fit + residual when decomposition is enabled
                // and bind the decomposed representation (residual at _BaseResidualMap, split
                // mesh in the after pane). Never writes to disk. Gated on decompWillRun so a
                // CR-01-guarded selection previews the Phase-3 shape Process generates.
                if (decompWillRun)
                {
                    RunDecompPreview(inspection);
                }

                _namerMaterial.SetTexture(SurfaceMapId, _liveResult.PackedSurface);
                if (_decompOutput != null && _decompOutput.Residual != null)
                {
                    _namerMaterial.SetTexture(BaseResidualMapId, _decompOutput.Residual);
                }
                else if (_decompOutput == null)
                {
                    // No decomposition ran (off or guard-skipped): the saved Phase-3
                    // material binds the base PNG at _BaseResidualMap, so the preview does too.
                    _namerMaterial.SetTexture(BaseResidualMapId, previewBaseMap);
                }
                else
                {
                    // CR-01 (04.1 review): decomposition ran and the residual collapsed —
                    // the one-texture outcome. The saved material leaves _BaseResidualMap
                    // unbound (white) so albedo = _BaseColor * vertexColor; binding the base
                    // here would double-multiply it with the split mesh's fitted vertex
                    // colors and render the preview darker than the generated .mat.
                    _namerMaterial.SetTexture(BaseResidualMapId, null);
                }
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

                ApplyDebugGates();

                _status = string.Empty;
                _statusIsError = false;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _status = "Processing failed: " + ex.Message;
                _statusIsError = true;
            }
            finally
            {
                _recomputing = false;
            }

            Repaint();
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
        }

        /// <summary>
        /// DIP-02: writes the shaded-view debug dip-switch values onto the live preview
        /// material and (when present) the generated After material. These are transient
        /// preview toggles — no <c>EditorUtility.SetDirty</c> — so the on-disk generated
        /// .mat keeps its neutral 1.0 defaults. Metallic/Emissive are additionally ANDed
        /// with their persisted step-gate contribution flags (shader-only, no pipeline stage).
        /// </summary>
        private void ApplyDebugGates()
        {
            float residual = _dbgResidualEnabled ? 1f : 0f;
            float roughness = _dbgRoughnessEnabled ? 1f : 0f;
            float ao = _dbgAoEnabled ? 1f : 0f;
            float metallic = (_metallicContributionEnabled && _dbgMetallicEnabled) ? 1f : 0f;
            float emissive = (_emissiveContributionEnabled && _dbgEmissiveEnabled) ? 1f : 0f;
            float vertexColor = _dbgVertexColorEnabled ? 1f : 0f;
            float normal = _dbgNormalEnabled ? 1f : 0f;
            float roughnessNeutral = PrimaryInspection != null ? PrimaryInspection.Roughness : 0.5f;

            if (_namerMaterial != null)
            {
                _namerMaterial.SetFloat(DbgEnableResidualId, residual);
                _namerMaterial.SetFloat(DbgEnableRoughnessId, roughness);
                _namerMaterial.SetFloat(DbgEnableAoId, ao);
                _namerMaterial.SetFloat(DbgEnableMetallicId, metallic);
                _namerMaterial.SetFloat(DbgEnableEmissiveId, emissive);
                _namerMaterial.SetFloat(DbgEnableVertexColorId, vertexColor);
                _namerMaterial.SetFloat(DbgEnableNormalId, normal);
                _namerMaterial.SetFloat(DbgRoughnessNeutralId, roughnessNeutral);
            }

            if (_generatedMaterial != null)
            {
                _generatedMaterial.SetFloat(DbgEnableResidualId, residual);
                _generatedMaterial.SetFloat(DbgEnableRoughnessId, roughness);
                _generatedMaterial.SetFloat(DbgEnableAoId, ao);
                _generatedMaterial.SetFloat(DbgEnableMetallicId, metallic);
                _generatedMaterial.SetFloat(DbgEnableEmissiveId, emissive);
                _generatedMaterial.SetFloat(DbgEnableVertexColorId, vertexColor);
                _generatedMaterial.SetFloat(DbgEnableNormalId, normal);
                _generatedMaterial.SetFloat(DbgRoughnessNeutralId, roughnessNeutral);
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
                _previewBaseRt = new RenderTexture(descriptor)
                {
                    // Same sweep protection as the pool: without DontSave the editor's
                    // unused-asset sweeps destroy this RT between repaints.
                    hideFlags = HideFlags.HideAndDontSave,
                };
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

        private void EnsureDecompPipeline()
        {
            if (_decompPipeline == null)
            {
                _decompPipeline = new NamerDecompPipeline();
            }
        }

        /// <summary>
        /// Releases the live decomposition preview: the pool-leased residual output and the
        /// in-memory split mesh are destroyed before the next recompute or window disable.
        /// </summary>
        private void ReleaseDecompPreview()
        {
            if (_decompOutput != null)
            {
                _decompOutput.Dispose();
                _decompOutput = null;
            }

            _projectionContext?.Decomp?.Dispose();
            _projectionContext = null;

            if (_previewSplitMesh != null)
            {
                DestroyImmediate(_previewSplitMesh);
                _previewSplitMesh = null;
            }

            _decompStats = null;
        }

        /// <summary>
        /// Runs the in-memory vertex-color fit + residual for the live preview (D-08/D-12).
        /// In the 04.2 projection path the context already produced the split/colors/residual
        /// inside <see cref="NamerComputePipeline.Process"/> — consume them (no second fit);
        /// in the legacy SobelEdge branch this mirrors <see cref="NamerProcessor"/>'s
        /// decomposition stage but keeps the residual render target bound to the preview
        /// material (no readback, no disk write).
        /// </summary>
        private void RunDecompPreview(NamerMaterialInspection inspection)
        {
            _decompStats = null;
            if (_previewMesh == null || inspection == null || _liveResult == null)
            {
                return;
            }

            EnsureDecompPipeline();

            if (_projectionContext != null)
            {
                // 04.2 projection path: the context already produced Split/Colors/Decomp
                // inside Process. Consume them for the preview mesh + residual binding
                // (no second fit — the projection IS the fit the preview renders).
                NamerProjectionContext ctx = _projectionContext;
                if (ctx.Decomp != null && ctx.Decomp.Stats.CannotDecompose)
                {
                    // CR-03 mirror: Process generates the Phase-3 shape here — preview must match.
                    _decompStats = ctx.Decomp.Stats;
                    return;
                }

                _decompOutput = ctx.Decomp;
                _decompStats = ctx.Decomp != null ? ctx.Decomp.Stats : null;
                if (ctx.Split != null && ctx.Colors != null)
                {
                    _previewSplitMesh = BuildPreviewSplitMesh(ctx.Split, ctx.Colors);
                }

                return;
            }

            // Legacy SobelEdge branch (decomposition on, no projection): in-memory fit +
            // residual, mode from the Write Residual checkbox (AlwaysKeep/NeverKeep).
            NativeArray<Color32> baseTexels = default;
            VertexColorFitResult fit = null;
            try
            {
                baseTexels = ReadBackBaseTexels(_liveResult.NormalizedBaseColor);
                NamerSplitResult split = MeshVertexSplitter.Split(_previewMesh);
                fit = VertexColorFitter.Fit(split, baseTexels, _liveResult.Width, _liveResult.Height);
                Color32[] colors = fit.ToColor32Array();
                _decompOutput = _decompPipeline.GenerateResidual(
                    split, colors, _liveResult.NormalizedBaseColor,
                    _liveResult.Width, _liveResult.Height,
                    _errorThreshold, _residualResolution,
                    projectedOut: null,
                    mode: _writeResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep);
                _decompStats = _decompOutput.Stats;
                _previewSplitMesh = BuildPreviewSplitMesh(split, colors);
            }
            finally
            {
                if (fit != null)
                {
                    fit.Dispose();
                }

                if (baseTexels.IsCreated)
                {
                    baseTexels.Dispose();
                }
            }
        }

        private static NativeArray<Color32> ReadBackBaseTexels(RenderTexture source)
        {
            AsyncGPUReadbackRequest request = NamerComputePipeline.RequestReadback(source, 0, TextureFormat.RGBA32);
            request.forcePlayerLoopUpdate = true;
            request.WaitForCompletion();

            if (request.hasError)
            {
                throw new InvalidOperationException("GPU readback failed while previewing decomposition.");
            }

            return request.GetData<Color32>();
        }

        private static Mesh BuildPreviewSplitMesh(NamerSplitResult split, Color32[] colors)
        {
            Mesh mesh = new Mesh { name = "NamerDecompPreview", hideFlags = HideFlags.HideAndDontSave };
            if (split.VertexCount > ushort.MaxValue)
            {
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            mesh.SetVertices(split.Positions);
            mesh.SetNormals(split.Normals);
            mesh.SetTangents(split.Tangents);
            mesh.SetUVs(0, split.Uvs);
            mesh.colors32 = colors;

            // bindposes must be assigned before boneWeights on a skinned mesh or a
            // SkinnedMeshRenderer may not render the swapped sharedMesh (gap 3b).
            if (split.Bindposes != null)
            {
                mesh.bindposes = split.Bindposes;
            }

            if (split.BoneWeights != null)
            {
                // Variable-count API (mirrors ApplySplitStreams): preserves >4 influences.
                // NativeArray-only overload — the mesh copies the data during the call.
                using (NativeArray<byte> bonesPerVertex = new NativeArray<byte>(split.BonesPerVertex, Allocator.Temp))
                using (NativeArray<BoneWeight1> boneWeights = new NativeArray<BoneWeight1>(split.BoneWeights, Allocator.Temp))
                {
                    mesh.SetBoneWeights(bonesPerVertex, boneWeights);
                }
            }

            mesh.subMeshCount = split.SubMeshTriangles.Length;
            for (int i = 0; i < split.SubMeshTriangles.Length; i++)
            {
                mesh.SetTriangles(split.SubMeshTriangles[i], i);
            }

            // Wireframe submesh (04.2 second pass): one extra line-topology submesh
            // after the triangle submeshes, drawn over the after pane with the unlit
            // NamerPreviewWire material when the Triangles toggle is on.
            int wireSubmesh = split.SubMeshTriangles.Length;
            mesh.subMeshCount = wireSubmesh + 1;
            mesh.SetIndices(BuildWireEdgeIndices(split), MeshTopology.Lines, wireSubmesh);

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds the line-list indices for the preview split mesh's wireframe submesh:
        /// every triangle's three edges — (a,b), (b,c), (c,a) — across all submeshes,
        /// sharing the mesh's vertex buffer (no vertex duplication). NO edge
        /// deduplication: interior shared edges overdraw the same wire color, which is
        /// invisible.
        /// </summary>
        private static int[] BuildWireEdgeIndices(NamerSplitResult split)
        {
            int totalTriangles = 0;
            for (int i = 0; i < split.SubMeshTriangles.Length; i++)
            {
                totalTriangles += split.SubMeshTriangles[i].Length / 3;
            }

            int[] edges = new int[totalTriangles * 6];
            int write = 0;
            for (int i = 0; i < split.SubMeshTriangles.Length; i++)
            {
                int[] triangles = split.SubMeshTriangles[i];
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    edges[write++] = triangles[t];
                    edges[write++] = triangles[t + 1];
                    edges[write++] = triangles[t + 1];
                    edges[write++] = triangles[t + 2];
                    edges[write++] = triangles[t + 2];
                    edges[write++] = triangles[t];
                }
            }

            return edges;
        }

        private void MarkDirty()
        {
            _dirty = true;
            _lastChange = EditorApplication.timeSinceStartup;
        }

        private void OnGUI()
        {
            NamerMaterialInspection inspection = PrimaryInspection;

            // D-07: the functional controls scroll, grouped into collapsible foldout sections
            // (state persisted via NamerProcessorSettings). The Process button + status stay
            // OUTSIDE the scroll view so the primary action is always reachable.
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawStepSwitches();
            DrawSourceSection();
            DrawPreviewSection(inspection);
            DrawRoughnessExtractionSection(inspection);
            DrawAoSection(inspection);
            DrawDecompositionSection(inspection);
            DrawOutputSection();
            EditorGUILayout.EndScrollView();

            DrawActionSection(inspection);
        }

        /// <summary>
        /// DIP-01 hard stage dip-switch row rendered above the foldouts. VC + Residual reuses
        /// the existing <see cref="NamerProcessorSettings.DecompositionEnabled"/> setting;
        /// Roughness/AO hard-gate the matching pipeline stages independently of their strength
        /// sliders; Metallic/Emissive gate the shader only (no pipeline stage exists). Each
        /// toggle writes through to its persisted setting.
        /// </summary>
        private void DrawStepSwitches()
        {
            EditorGUI.BeginDisabledGroup(_busy);
            EditorGUILayout.BeginHorizontal();

            bool newDecomposition = EditorGUILayout.ToggleLeft(
                new GUIContent("VC + Residual",
                    "Hard stage gate for vertex-color decomposition + residual (reuses DecompositionEnabled). Independent of the strength sliders."),
                _decompositionEnabled);
            if (newDecomposition != _decompositionEnabled)
            {
                _decompositionEnabled = newDecomposition;
                _settings.DecompositionEnabled = newDecomposition;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            bool newRoughness = EditorGUILayout.ToggleLeft(
                new GUIContent("Roughness",
                    "Hard stage gate for roughness extraction — skips extraction without touching the strength slider."),
                _roughnessStageEnabled);
            if (newRoughness != _roughnessStageEnabled)
            {
                _roughnessStageEnabled = newRoughness;
                _settings.RoughnessStageEnabled = newRoughness;
                MarkDirty();
            }

            bool newAo = EditorGUILayout.ToggleLeft(
                new GUIContent("AO",
                    "Hard stage gate for AO un-multiply — skips the un-multiply without touching the strength slider."),
                _aoStageEnabled);
            if (newAo != _aoStageEnabled)
            {
                _aoStageEnabled = newAo;
                _settings.AoStageEnabled = newAo;
                MarkDirty();
            }

            bool newMetallic = EditorGUILayout.ToggleLeft(
                new GUIContent("Metallic",
                    "Shader-only gate for the metallic contribution (no pipeline stage exists)."),
                _metallicContributionEnabled);
            if (newMetallic != _metallicContributionEnabled)
            {
                _metallicContributionEnabled = newMetallic;
                _settings.MetallicContributionEnabled = newMetallic;
                ApplyDebugGates();
                Repaint();
            }

            bool newEmissive = EditorGUILayout.ToggleLeft(
                new GUIContent("Emissive",
                    "Shader-only gate for the emissive contribution (no pipeline stage exists)."),
                _emissiveContributionEnabled);
            if (newEmissive != _emissiveContributionEnabled)
            {
                _emissiveContributionEnabled = newEmissive;
                _settings.EmissiveContributionEnabled = newEmissive;
                ApplyDebugGates();
                Repaint();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
        }

        private void DrawSourceSection()
        {
            _settings.FoldoutSource = EditorGUILayout.Foldout(_settings.FoldoutSource, "Source", true);
            if (!_settings.FoldoutSource)
            {
                return;
            }

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
            _settings.FoldoutPreview = EditorGUILayout.Foldout(_settings.FoldoutPreview, "Preview/Debug", true);
            if (!_settings.FoldoutPreview)
            {
                return;
            }

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

            float previewWidth = Mathf.Max(EditorGUIUtility.currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth, 256f);
            Rect previewRect = GUILayoutUtility.GetRect(previewWidth, previewWidth * 0.5f);
            HandlePreviewCameraInput(previewRect);

            Material beforeMaterial = inspection.Material;
            Material afterMaterial = (_afterPanelState.PreferGenerated && _generatedMaterial != null)
                ? _generatedMaterial
                : _namerMaterial;

            if (afterMaterial == null)
            {
                afterMaterial = beforeMaterial;
            }

            if (beforeMaterial == null)
            {
                GUI.Box(previewRect, GUIContent.none);
                return;
            }

            // D-08 after-mesh parity: when decomposition is ON, the after pane draws the
            // split mesh (vertex colors), falling back to the generated split mesh once a
            // Process wrote one, and to the source mesh otherwise.
            Mesh afterMesh = _previewMesh;
            if (_decompositionEnabled)
            {
                if (_afterPanelState.PreferGenerated && _generatedMesh != null)
                {
                    afterMesh = _generatedMesh;
                }
                else if (_previewSplitMesh != null)
                {
                    afterMesh = _previewSplitMesh;
                }
            }

            // Wireframe second pass (04.2): renders over whatever mesh the After pane
            // shows — the split mesh's own line-topology submesh, or a cached standalone
            // wire mesh built from the source/generated mesh (assets are never mutated).
            Mesh wireMesh = null;
            Material wireMaterial = null;
            int wireSubmesh = -1;
            if (_showTriangles && afterMesh != null)
            {
                EnsureWireMaterial();
                wireMaterial = _wireMaterial;
                if (afterMesh == _previewSplitMesh)
                {
                    wireMesh = afterMesh;
                    wireSubmesh = afterMesh.subMeshCount - 1;
                }
                else
                {
                    wireMesh = GetOrCreatePreviewWireMesh(afterMesh);
                    wireSubmesh = 0;
                }
            }

            PreviewRenderResult previewResult = _preview.Render(_previewMesh, afterMesh, beforeMaterial, afterMaterial, previewRect, wireMesh, wireMaterial, wireSubmesh);
            if (previewResult.IsValid)
            {
                DrawPreviewPaneTexture(previewRect, previewResult);
                DrawPreviewPaneOutline(previewRect);
            }
            else
            {
                GUI.Box(previewRect, GUIContent.none);
            }

            EditorGUI.BeginDisabledGroup(_busy);
            EditorGUILayout.BeginHorizontal();
            DrawShaderInputToggle(0, ref _dbgResidualEnabled);
            DrawShaderInputToggle(1, ref _dbgRoughnessEnabled);
            DrawShaderInputToggle(2, ref _dbgAoEnabled);
            DrawShaderInputToggle(3, ref _dbgMetallicEnabled);
            DrawShaderInputToggle(4, ref _dbgEmissiveEnabled);
            DrawShaderInputToggle(5, ref _dbgVertexColorEnabled);
            DrawShaderInputToggle(6, ref _dbgNormalEnabled);
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();
            DrawChannelPanes();
            EditorGUILayout.LabelField(
                new GUIContent("Toggles neutralize the matching input in the full shaded After view."),
                EditorStyles.miniLabel);

            bool newShowTriangles = EditorGUILayout.Toggle(
                new GUIContent("Triangles",
                    "Renders the split mesh's triangle edges as a second pass in the After preview (visual debug only)."),
                _showTriangles);
            if (newShowTriangles != _showTriangles)
            {
                _showTriangles = newShowTriangles;
                Repaint();
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Renders one shaded-view input toggle (DIP-02) from
        /// <see cref="ShaderInputToggleLabels"/>. On change it writes the gate onto the
        /// preview materials and repaints — shader-only, no GPU recompute, not persisted.
        /// </summary>
        private void DrawShaderInputToggle(int index, ref bool enabled)
        {
            bool newValue = EditorGUILayout.ToggleLeft(
                new GUIContent(ShaderInputToggleLabels[index],
                    "Neutralizes the '" + ShaderInputToggleLabels[index]
                        + "' input in the full shaded After view (shader-only, no GPU recompute)."),
                enabled);
            if (newValue != enabled)
            {
                enabled = newValue;
                ApplyDebugGates();
                Repaint();
            }
        }

        /// <summary>
        /// D-12 channel pane row: six small panes directly under the shaded-view toggle
        /// row, each drawn through the <c>NamerChannelView</c> material so the pane shows
        /// exactly what the After material's packed textures decode to via the shared
        /// NAMER decode (no drift from the runtime material). Neutral no-op boxes when
        /// the After material has no generated textures yet. Clicking a pane opens a
        /// large popup of that channel (click-away closes).
        /// </summary>
        private void DrawChannelPanes()
        {
            Material afterMaterial = (_afterPanelState.PreferGenerated && _generatedMaterial != null)
                ? _generatedMaterial
                : _namerMaterial;

            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < ChannelPaneLabels.Length; i++)
            {
                if (i == 5)
                {
                    // No pane for the Vertex Color column; reserve its slot so the
                    // Normal pane still lands under the Normal toggle.
                    GUILayoutUtility.GetRect(new GUIContent(ShaderInputToggleLabels[5]),
                        EditorStyles.toggle, GUILayout.Height(ChannelPaneSize));
                }

                int toggleIndex = i < 5 ? i : 6;
                Rect slot = GUILayoutUtility.GetRect(
                    new GUIContent(ShaderInputToggleLabels[toggleIndex]),
                    EditorStyles.toggle, GUILayout.Height(ChannelPaneSize));
                Rect paneRect = new Rect(slot.x, slot.y,
                    Mathf.Min(ChannelPaneSize, slot.width), ChannelPaneSize);

                if (afterMaterial == null || afterMaterial.GetTexture(SurfaceMapId) == null)
                {
                    // No generated textures yet: neutral no-op box, no draw/tooltip/click.
                    GUI.Box(paneRect, GUIContent.none);
                    continue;
                }

                EnsureChannelViewMaterial();
                _channelViewMaterial.SetTexture(SurfaceMapId, afterMaterial.GetTexture(SurfaceMapId));
                _channelViewMaterial.SetTexture(BaseResidualMapId, afterMaterial.GetTexture(BaseResidualMapId));
                _channelViewMaterial.SetFloat(OcclusionStrengthId, afterMaterial.GetFloat(OcclusionStrengthId));
                _channelViewMaterial.SetFloat(ChannelId, i);
                if (Event.current.type == EventType.Repaint)
                {
                    Graphics.DrawTexture(paneRect, Texture2D.whiteTexture, _channelViewMaterial);
                }

                GUI.Label(paneRect, new GUIContent(string.Empty,
                    ChannelPaneLabels[i] + " — source: " + (i == 0 ? "_BaseResidualMap" : "_SurfaceMap")));
                EditorGUIUtility.AddCursorRect(paneRect, MouseCursor.Zoom);

                if (Event.current.type == EventType.MouseDown && paneRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    ShowChannelPopup(paneRect, i, afterMaterial);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Lazily creates the hidden <c>NamerChannelView</c> material used by the
        /// channel panes. The popup creates and owns its own instance.
        /// </summary>
        private void EnsureChannelViewMaterial()
        {
            if (_channelViewMaterial == null)
            {
                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NamerChannelView");
                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "Shader 'GraffitiEntertainment.Namer/NamerChannelView' was not found. Ensure the channel-view shader compiled and imported.");
                }

                _channelViewMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        /// <summary>
        /// Lazily creates the hidden <c>NamerPreviewWire</c> material used by the
        /// After-pane wireframe second pass, with the wire color from
        /// <see cref="TriangleWireframeColor"/>.
        /// </summary>
        private void EnsureWireMaterial()
        {
            if (_wireMaterial == null)
            {
                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NamerPreviewWire");
                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "Shader 'GraffitiEntertainment.Namer/NamerPreviewWire' was not found. Ensure the wire shader compiled and imported.");
                }

                _wireMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _wireMaterial.SetColor(WireColorId, TriangleWireframeColor);
            }
        }

        /// <summary>
        /// Builds (and caches) a standalone hidden line-topology mesh carrying every
        /// triangle edge of <paramref name="source"/> — the wireframe second pass for
        /// After-pane states that show the source or generated mesh (they carry no
        /// wire submesh and are never mutated). Cached per source instance so
        /// repaints do not rebuild it; rebuilt when the after mesh changes; disposed
        /// in OnDisable.
        /// </summary>
        private Mesh GetOrCreatePreviewWireMesh(Mesh source)
        {
            if (source == null)
            {
                return null;
            }

            if (_previewWireMesh != null && _previewWireSource == source)
            {
                return _previewWireMesh;
            }

            if (_previewWireMesh != null)
            {
                DestroyImmediate(_previewWireMesh);
            }

            Mesh wire = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            if (source.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32)
            {
                wire.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            }

            wire.SetVertices(source.vertices);
            wire.subMeshCount = 1;
            wire.SetIndices(BuildWireEdgeIndices(source), MeshTopology.Lines, 0);
            wire.RecalculateBounds();
            _previewWireSource = source;
            _previewWireMesh = wire;
            return wire;
        }

        /// <summary>
        /// Line-list indices for a wire mesh built from <paramref name="mesh"/>: every
        /// triangle's three edges — (a,b), (b,c), (c,a) — across all submeshes, sharing
        /// the source vertex positions. No edge deduplication (interior shared edges
        /// overdraw the same wire color, which is invisible).
        /// </summary>
        private static int[] BuildWireEdgeIndices(Mesh mesh)
        {
            int totalTriangles = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                totalTriangles += (int)mesh.GetIndexCount(i) / 3;
            }

            int[] edges = new int[totalTriangles * 6];
            int write = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                int[] triangles = mesh.GetTriangles(i);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    edges[write++] = triangles[t];
                    edges[write++] = triangles[t + 1];
                    edges[write++] = triangles[t + 1];
                    edges[write++] = triangles[t + 2];
                    edges[write++] = triangles[t + 2];
                    edges[write++] = triangles[t];
                }
            }

            return edges;
        }

        /// <summary>
        /// Opens the large channel popup anchored below the clicked pane, capturing the
        /// After material's textures/occlusion strength so the popup is immune to later
        /// material edits.
        /// </summary>
        private static void ShowChannelPopup(Rect paneRect, int channel, Material afterMaterial)
        {
            ChannelViewPopup.Show(
                paneRect,
                afterMaterial.GetTexture(SurfaceMapId),
                afterMaterial.GetTexture(BaseResidualMapId),
                afterMaterial.GetFloat(OcclusionStrengthId),
                channel);
        }

        /// <summary>
        /// The click-to-open large channel view (D-12). Shown via
        /// <see cref="EditorWindow.ShowAsDropDown"/> anchored below the clicked pane, so
        /// the framework handles click-away dismissal and only one popup is ever open
        /// (a new show closes the previous). Creates, owns, and disposes its own
        /// material so the popup never leaks editor resources.
        /// </summary>
        private sealed class ChannelViewPopup : EditorWindow
        {
            private static ChannelViewPopup _activePopup;

            private Texture _surface;
            private Texture _baseResidual;
            private float _occlusionStrength;
            private int _channel;
            private Material _material;

            /// <summary>
            /// Creates and shows the popup anchored below the clicked pane (GUI space,
            /// via ShowAsDropDown), capturing the After material's channel inputs at
            /// click time. Any already-open popup is closed first, so popups replace
            /// each other instead of accumulating.
            /// </summary>
            public static void Show(Rect paneRect, Texture surface, Texture baseResidual,
                float occlusionStrength, int channel)
            {
                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NamerChannelView");
                if (shader == null)
                {
                    throw new InvalidOperationException(
                        "Shader 'GraffitiEntertainment.Namer/NamerChannelView' was not found. Ensure the channel-view shader compiled and imported.");
                }

                if (_activePopup != null)
                {
                    _activePopup.Close();
                    _activePopup = null;
                }

                ChannelViewPopup popup = CreateInstance<ChannelViewPopup>();
                popup._surface = surface;
                popup._baseResidual = baseResidual;
                popup._occlusionStrength = occlusionStrength;
                popup._channel = channel;
                popup._material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

                // ShowAsDropDown anchors in SCREEN space, but the pane rect is GUI space
                // (window-local) — convert first or the popup lands at that raw point on
                // the desktop instead of over the NAMER panel. Horizontally center the
                // popup on the clicked pane; ShowAsDropDown itself clamps to the screen.
                Vector2 screenPos = GUIUtility.GUIToScreenPoint(new Vector2(paneRect.x, paneRect.y));
                float anchorX = screenPos.x + paneRect.width * 0.5f - ChannelPopupSize * 0.5f;
                Rect screenAnchor = new Rect(anchorX, screenPos.y, paneRect.width, paneRect.height);
                popup.ShowAsDropDown(screenAnchor, new Vector2(ChannelPopupSize, ChannelPopupSize));
                _activePopup = popup;
            }

            private void OnGUI()
            {
                Rect rect = new Rect(0f, 0f, position.width, position.height);

                _material.SetTexture(SurfaceMapId, _surface);
                _material.SetTexture(BaseResidualMapId, _baseResidual);
                _material.SetFloat(OcclusionStrengthId, _occlusionStrength);
                _material.SetFloat(ChannelId, _channel);

                if (Event.current.type == EventType.Repaint)
                {
                    Graphics.DrawTexture(rect, Texture2D.whiteTexture, _material);

                    // Window-style 1px outline (same treatment as the preview panes) so
                    // the popup edge reads clearly against the panel content underneath.
                    Color outline = new Color(0.4f, 0.4f, 0.4f, 1f);
                    EditorGUI.DrawRect(new Rect(0f, 0f, position.width, 1f), outline);                     // top
                    EditorGUI.DrawRect(new Rect(0f, position.height - 1f, position.width, 1f), outline);   // bottom
                    EditorGUI.DrawRect(new Rect(0f, 0f, 1f, position.height), outline);                    // left
                    EditorGUI.DrawRect(new Rect(position.width - 1f, 0f, 1f, position.height), outline);   // right
                }
            }

            private void OnDisable()
            {
                if (_material != null)
                {
                    DestroyImmediate(_material);
                    _material = null;
                }

                if (_activePopup == this)
                {
                    _activePopup = null;
                }
            }
        }

        private void DrawPreviewPaneOutline(Rect previewRect)
        {
            Color outline = new Color(0.4f, 0.4f, 0.4f, 1f);
            EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.y, previewRect.width, 1f), outline);                       // top
            EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.yMax - 1f, previewRect.width, 1f), outline);                // bottom
            EditorGUI.DrawRect(new Rect(previewRect.x, previewRect.y, 1f, previewRect.height), outline);                       // left
            EditorGUI.DrawRect(new Rect(previewRect.xMax - 1f, previewRect.y, 1f, previewRect.height), outline);               // right
            EditorGUI.DrawRect(new Rect(previewRect.x + previewRect.width / 2f - 0.5f, previewRect.y, 1f, previewRect.height), outline); // center divider
        }

        private void DrawPreviewPaneTexture(Rect previewRect, PreviewRenderResult result)
        {
            Rect leftPane = new Rect(previewRect.x, previewRect.y, previewRect.width * 0.5f, previewRect.height);
            Rect rightPane = new Rect(previewRect.x + previewRect.width * 0.5f, previewRect.y, previewRect.width * 0.5f, previewRect.height);
            GUI.DrawTextureWithTexCoords(leftPane, result.Before, new Rect(0f, 0f, 0.5f, 1f));   // before pane: before RT's left half
            GUI.DrawTextureWithTexCoords(rightPane, result.After, new Rect(0.5f, 0f, 0.5f, 1f)); // after pane: after RT's right half
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
                if (current.control)
                {
                    // Ctrl+drag strafes the shared camera; pixel delta converted to world
                    // units at the current orthographic size (GUI y is down).
                    float aspect = previewRect.width / Mathf.Max(previewRect.height, 1f);
                    float orthoSize = _preview.OrthographicSizeForAspect(aspect, previewRect.height);
                    float worldPerPixel = 2f * orthoSize / Mathf.Max(previewRect.height, 1f);
                    _preview.Pan(-current.delta.x * worldPerPixel, current.delta.y * worldPerPixel);
                }
                else
                {
                    _preview.Orbit(current.delta.x, current.delta.y);
                }

                current.Use();
                Repaint();
            }
            else if (current.type == EventType.ScrollWheel
                && (current.shift || current.control || current.alt))
            {
                // Negated so wheel-up (Unity reports negative delta.y) zooms IN — the
                // convention users expect from editors and viewers.
                _preview.Zoom(-current.delta.y);
                current.Use();
                Repaint();
            }
        }

        private void DrawRoughnessExtractionSection(NamerMaterialInspection inspection)
        {
            _settings.FoldoutRoughnessExtraction = EditorGUILayout.Foldout(_settings.FoldoutRoughnessExtraction, "Roughness Extraction", true);
            if (!_settings.FoldoutRoughnessExtraction)
            {
                return;
            }

            EditorGUI.BeginDisabledGroup(inspection == null || _busy);

            int newDipSource = EditorGUILayout.Popup(
                new GUIContent(
                    "Dip Source",
                    "Which signal dips roughness toward gloss. Removed Detail (default) re-expresses the luminance "
                        + "the Gouraud projection removed as gloss — bright speckle dips toward gloss, dark occlusion "
                        + "raises toward matte — and requires Vertex Color Decomposition to be enabled. Sobel Edge is "
                        + "the fallback Blender-parity edge signal, used when decomposition is off (or skipped). "
                        + "Recomputes the preview in memory " + NamerEditorConstants.DebounceSeconds
                        + " s after the change — nothing is written to disk."),
                (int)_dipSource,
                new[] { "Removed Detail", "Sobel Edge" });
            if (newDipSource != (int)_dipSource)
            {
                _dipSource = (NamerDipSource)newDipSource;
                _settings.DipSource = newDipSource;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            float newStrength = EditorGUILayout.Slider(
                new GUIContent(
                    "Roughness Dip Depth",
                    "Taste control — how strongly removed-detail luminance is re-expressed as gloss (bright "
                        + "speckle dips toward gloss, dark occlusion raises toward matte; 0 = keep the authored "
                        + "roughness scalar). Luminance carries roughly half of the removed signal's energy "
                        + "(Neo: ~55%, p10 37%); the discarded chroma grain averages ~0.07 linear — an accepted "
                        + "loss, because a scalar gloss channel has no home for color. Recomputes the preview in "
                        + "memory " + NamerEditorConstants.DebounceSeconds
                        + " s after the slider stops — nothing is written to disk."),
                _roughnessExtractStrength, 0f, 1f);
            if (!Mathf.Approximately(newStrength, _roughnessExtractStrength))
            {
                _roughnessExtractStrength = newStrength;
                _settings.RoughnessExtractStrength = newStrength;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            bool removedDetailEffective = _dipSource == NamerDipSource.RemovedDetail
                && _decompositionEnabled
                && string.IsNullOrEmpty(_decompGuardReason);
            if (_dipSource == NamerDipSource.SobelEdge)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "Sobel Edge is a fallback dip source — the Removed Detail path requires Vertex Color Decomposition.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }
            else if (!removedDetailEffective)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox(
                    "The effective dip source is Sobel Edge: Removed Detail requires Vertex Color Decomposition, "
                        + "which is currently off or skipped for this selection.",
                    MessageType.Info);
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndDisabledGroup();
        }

        private void DrawAoSection(NamerMaterialInspection inspection)
        {
            _settings.FoldoutAo = EditorGUILayout.Foldout(_settings.FoldoutAo, "AO", true);
            if (!_settings.FoldoutAo)
            {
                return;
            }

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

            EditorGUI.EndDisabledGroup();
        }

        private void DrawDecompositionSection(NamerMaterialInspection inspection)
        {
            _settings.FoldoutDecomposition = EditorGUILayout.Foldout(_settings.FoldoutDecomposition, "Decomposition", true);
            if (!_settings.FoldoutDecomposition)
            {
                return;
            }

            EditorGUI.BeginDisabledGroup(inspection == null || _busy);

            bool newDecomp = EditorGUILayout.Toggle(
                new GUIContent(
                    "Vertex Color Decomposition",
                    "Fit the base color into mesh vertex colors and reconstruct it with a residual texture. When enabled, the preview shows the decomposed reconstruction and Process writes the seam-split mesh + residual EXR. When both representations exist, this switch flips between them without reprocessing."),
                _decompositionEnabled);
            if (newDecomp != _decompositionEnabled)
            {
                _decompositionEnabled = newDecomp;
                _settings.DecompositionEnabled = newDecomp;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            // CR-01 truth-telling: tell the user up front (the preview and Process both
            // show/generate the non-decomposed shape) instead of letting Process silently
            // fall back while the preview looked extracted.
            if (_decompositionEnabled && !string.IsNullOrEmpty(_decompGuardReason))
            {
                EditorGUILayout.HelpBox(
                    "Vertex-color decomposition will be skipped when processing this selection: "
                    + _decompGuardReason
                    + ". The preview shows the non-decomposed result Process generates.",
                    MessageType.Warning);
            }

            EditorGUI.BeginDisabledGroup(!_decompositionEnabled);

            bool newWriteResidual = EditorGUILayout.Toggle(
                new GUIContent(
                    "Write Residual Texture",
                    "OFF (default) = one-texture outcome: no residual EXR — the Gouraud projection already makes the "
                        + "base ÷ vertex-color interpolation white by construction. ON = additionally write source ÷ "
                        + "vertex-color interpolation as an EXR carrying the removed detail (including its luminance, "
                        + "which is also re-expressed as gloss). On stacked-UV assets most covered texels are covered "
                        + "by more than one triangle (Neo: 87.9%), and the rasterized surface keeps only the "
                        + "first-covering triangle's interpolation — so the EXR reads honestly but cannot attribute "
                        + "overlap blending."),
                _writeResidual);
            if (newWriteResidual != _writeResidual)
            {
                _writeResidual = newWriteResidual;
                _settings.WriteResidual = newWriteResidual;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            // Error Threshold + Residual Resolution only act when residual writing is on
            // (04.2: the checkbox is the residual gate now; the threshold/resolution search
            // keys on source-reconstruction error for the written EXR). They are rendered
            // disabled (EditorGUI.DisabledScope) with a one-line hint when Write Residual is
            // off — matching the phase's disable-state conventions.
            using (new EditorGUI.DisabledScope(!_writeResidual))
            {
                float newThreshold = EditorGUILayout.Slider(
                    new GUIContent(
                        "Error Threshold",
                        "Maximum acceptable reconstruction error for the adaptive residual-resolution search (used when "
                            + "Write Residual is on). Recomputes the preview in memory "
                            + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."),
                    _errorThreshold, 0f, 0.10f);
                if (!Mathf.Approximately(newThreshold, _errorThreshold))
                {
                    _errorThreshold = newThreshold;
                    _settings.ErrorThreshold = newThreshold;
                    _afterPanelState.MarkTweaking();
                    MarkDirty();
                }

                int newResolution = EditorGUILayout.Popup(
                    new GUIContent(
                        "Residual Resolution",
                        "Residual texture resolution. Auto adaptively halves from the source resolution while error stays within the threshold; manual options snap to the same halving steps."),
                    _residualResolution,
                    new[] { "Auto", "2048", "1024", "512", "256", "128" });
                if (newResolution != _residualResolution)
                {
                    _residualResolution = newResolution;
                    _settings.ResidualResolution = newResolution;
                    _afterPanelState.MarkTweaking();
                    MarkDirty();
                }
            }

            if (!_writeResidual)
            {
                EditorGUILayout.LabelField(
                    new GUIContent("Only applies when Write Residual is on."),
                    EditorStyles.miniLabel);
            }

            if (_decompositionEnabled)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
                DrawDecompStats();
            }

            EditorGUI.EndDisabledGroup();

            EditorGUI.EndDisabledGroup();

            // Visible feedback for the otherwise-invisible debounced preview recompute:
            // pending/recomputing while dirty, settled once the compute finishes.
            EditorGUILayout.LabelField(
                _dirty || _recomputing ? "Recomputing preview…" : "Preview up to date",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Renders the five read-only decomposition statistics rows (D-09 / VCOL-04), relabeled
        /// in 04.2 to removed-detail semantics. Values show "—" until the first fit completes;
        /// the residual row reads <see cref="ResidualNotWrittenLabel"/> when the residual is not
        /// written (one-texture) and "written @ Npx" when the EXR is written.
        /// </summary>
        private void DrawDecompStats()
        {
            if (_decompStats == null)
            {
                EditorGUILayout.LabelField(DecompStatLabels[0], "—");
                EditorGUILayout.LabelField(DecompStatLabels[1], "—");
                EditorGUILayout.LabelField(DecompStatLabels[2], "—");
                EditorGUILayout.LabelField(DecompStatLabels[3], "—");
                EditorGUILayout.LabelField(DecompStatLabels[4], "—");
                return;
            }

            bool residualWritten = _decompStats.ResidualRequired && _writeResidual;

            EditorGUILayout.LabelField(
                new GUIContent(DecompStatLabels[0],
                    "Fraction of UV-covered texels reconstructed within the error threshold."),
                new GUIContent((_decompStats.Coverage * 100f).ToString("0") + "%"));
            EditorGUILayout.LabelField(
                new GUIContent(DecompStatLabels[1],
                    "Average removed-detail reconstruction error over covered texels (the same "
                    + "source-vs-reconstruction error the residual-ON EXR encodes)."),
                new GUIContent(_decompStats.AvgError.ToString("0.000")));
            EditorGUILayout.LabelField(
                new GUIContent(DecompStatLabels[2],
                    "Maximum removed-detail reconstruction error over covered texels (the same "
                    + "source-vs-reconstruction error the residual-ON EXR encodes)."),
                new GUIContent(_decompStats.MaxError.ToString("0.000")));
            EditorGUILayout.LabelField(
                new GUIContent(DecompStatLabels[3],
                    "The source-vs-reconstruction error of the removed detail: what the residual-ON "
                    + "EXR encodes and what the gloss transfer re-expresses."),
                new GUIContent(_decompStats.FitOnlyMaxError.ToString("0.000")));
            EditorGUILayout.LabelField(
                new GUIContent(DecompStatLabels[4],
                    "Whether the residual EXR was written (the 04.2 Write Residual checkbox)."),
                new GUIContent(residualWritten
                    ? "written @" + _decompStats.ChosenResolution + "px"
                    : ResidualNotWrittenLabel));
        }

        private void DrawOutputSection()
        {
            _settings.FoldoutOutput = EditorGUILayout.Foldout(_settings.FoldoutOutput, "Output", true);
            if (!_settings.FoldoutOutput)
            {
                return;
            }

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
                Debug.LogException(ex);
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
            return (string.IsNullOrEmpty(path) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) ? null : FindMeshSubAsset(path);
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
