using System;
using System.Globalization;
using System.Text;
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
        /// Pane channel (<see cref="ChannelPaneLabels"/> index) for each
        /// <see cref="ShaderInputToggleLabels"/> column, or -1 when that input has
        /// no packed-texture view — Vertex Color decodes from mesh vertex data, so
        /// there is no texture to render for its column.
        /// </summary>
        private static readonly int[] ChannelPaneForToggle = { 0, 1, 2, 3, 4, -1, 5 };

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

        /// <summary>
        /// Albedo preview caption when the preview lens is Stylized NAMER. Locked copy,
        /// em-dash verbatim; read via reflection by <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal const string AlbedoPreviewStylizedCaption = "Stylized albedo — palette transfer applied";

        /// <summary>
        /// Albedo preview caption when the preview lens is NAMER (the un-stylized
        /// baseline). Locked copy, em-dash verbatim; read via reflection by
        /// <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal const string AlbedoPreviewUnstylizedCaption = "Un-stylized albedo — After View (Preview/Debug) is NAMER";

        /// <summary>
        /// Albedo preview reason line when no Source is picked (Task 7: a domain reload
        /// wipes scene-object picks — the accepted Task 1 limitation). Locked copy; read
        /// via reflection by <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal const string AlbedoPreviewNoSourceCaption = "select a Source above to preview";

        /// <summary>
        /// Albedo preview reason line when a Source is picked but no preview recompute
        /// has produced a live result yet. Locked copy, em-dash verbatim; read via
        /// reflection by <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal const string AlbedoPreviewNoResultCaption = "no preview yet — waiting for recompute";

        /// <summary>
        /// Albedo preview caption when the preview lens is Stylized + Post (05.1 W7,
        /// D-04 lens honesty): the 2D albedo preview stays post-free — no simulation
        /// this phase. Locked copy, em-dash verbatim; read via reflection by
        /// <c>NamerPostPreviewTests</c>.
        /// </summary>
        internal const string AlbedoPreviewPostCaption = "Stylized albedo — the fitted post look renders in the 3D After pane only";

        /// <summary>
        /// Albedo preview button label that flips the After View lens to Stylized NAMER.
        /// Locked copy; read via reflection by <c>NamerEditorWindowSmokeTests</c>.
        /// </summary>
        internal const string AlbedoPreviewViewStylizedButton = "View Stylized";

        private static readonly int SurfaceMapId = Shader.PropertyToID("_SurfaceMap");
        private static readonly int BaseResidualMapId = Shader.PropertyToID("_BaseResidualMap");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_OcclusionStrength");
        private static readonly int AoUnmultiplyStrengthId = Shader.PropertyToID("_AoUnmultiplyStrength");
        private static readonly int DbgEnableResidualId = Shader.PropertyToID("_DbgEnableResidual");
        private static readonly int DbgEnableRoughnessId = Shader.PropertyToID("_DbgEnableRoughness");
        private static readonly int DbgEnableAoId = Shader.PropertyToID("_DbgEnableAO");
        private static readonly int DbgEnableMetallicId = Shader.PropertyToID("_DbgEnableMetallic");
        private static readonly int DbgEnableEmissiveId = Shader.PropertyToID("_DbgEnableEmissive");
        private static readonly int DbgEnableVertexColorId = Shader.PropertyToID("_DbgEnableVertexColor");
        private static readonly int DbgEnableNormalId = Shader.PropertyToID("_DbgEnableNormal");
        private static readonly int DbgRoughnessNeutralId = Shader.PropertyToID("_DbgRoughnessNeutral");
        private static readonly int PostEnableId = Shader.PropertyToID("_PostEnable");
        private static readonly int PostLmsCoeffsId = Shader.PropertyToID("_PostLmsCoeffs");
        private static readonly int PostExposureEvId = Shader.PropertyToID("_PostExposureEv");
        private static readonly int PostContrastZId = Shader.PropertyToID("_PostContrastZ");
        private static readonly int PostSaturationSId = Shader.PropertyToID("_PostSaturationS");
        private static readonly int PostCurveTexId = Shader.PropertyToID("_PostCurveTex");
        private static readonly int ChannelId = Shader.PropertyToID("_Channel");
        private static readonly int UvRectId = Shader.PropertyToID("_UvRect");
        private static readonly int WireColorId = Shader.PropertyToID("_WireColor");
        private static readonly Color TriangleWireframeColor = new Color(0f, 1f, 1f, 1f);

        // Debug-strip grid (2026-09-28 user amendment): ONE row of seven
        // kDebugColumnWidth columns separated by kDebugColumnGap — checkbox+label
        // above (own automatic rect, pinned to the column width), channel pane
        // centered below. DrawStepSwitches pins its five gate toggles to the same
        // grid (columns 0-4).
        private const float ChannelPaneSize = 64f;
        private const float ChannelPopupSize = 384f;
        private const float kDebugColumnWidth = 96f;
        private const float kDebugColumnGap = 10f;
        private const float kCoverageSliderStep = 0.005f;
        private const float kAlbedoPreviewHeight = 512f;

        // 05-03 W4 "After View" popup indices (preview-only lens; 0 = the un-stylized
        // NAMER baseline, the shipped default). 05.1 W5: 2 = Stylized + Post — the
        // fitted post grade applied in-shader (D-04: the ONLY post-carrying lens).
        internal const int kAfterViewNamer = 0;
        internal const int kAfterViewStylized = 1;
        internal const int kAfterViewPost = 2;

        /// <summary>
        /// 05-03 W4 / 05.1 W5 "After View" popup entries, index-parallel to
        /// <see cref="kAfterViewNamer"/>/<see cref="kAfterViewStylized"/>/
        /// <see cref="kAfterViewPost"/>. Read by <c>NamerPostPreviewTests</c> for the
        /// three-entry order pin.
        /// </summary>
        internal static readonly string[] AfterViewOptions =
        {
            "NAMER", "Stylized NAMER", "Stylized + Post",
        };

        /// <summary>
        /// Curve-strip texel count for the post-preview <c>_PostCurveTex</c> (05.1):
        /// URP's own TextureCurve bakes the same 128-texel width (k_Precision).
        /// </summary>
        private const int kPostCurveTexelCount = 128;

        // 05.1 one-slot curve-strip cache (T-05.1-04-02): rebuilt when the Style
        // Asset instance OR any of its four fitted curves changes (the cache key folds in
        // every key of every curve), released in OnDisable. HideAndDontSave texture —
        // transient preview state, never persisted.
        private static Texture2D _postCurveTexture;
        private static string _postCurveTextureKey;

        /// <summary>Fit Look progress-bar stage count (05.1 plan 03): references, stylized preview, fit.</summary>
        private const int kPostFitStageCount = 3;

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
        private RenderTexture _stylizePreviewBaseRt;
        private Material _channelViewMaterial;
        private Material _wireMaterial;
        private Mesh _previewWireSource;
        private Mesh _previewWireMesh;

        // Explicit user pick: asset selections survive domain reload; scene refs cannot (accepted EditorWindow serialization limitation).
        [SerializeField]
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
        private int _afterView;
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
        private float _coverageTarget = NamerEditorConstants.DefaultCoverageTarget;
        private int _residualResolution;
        private float _roughnessExtractStrength = NamerEditorConstants.DefaultRoughnessExtractStrength;
        private NamerDipSource _dipSource;
        private bool _writeResidual;
        private Vector2 _scrollPosition;
        // Scroll-view WIDGET rect (captured after EndScrollView) for the visible-band math in DrawAlbedoPreview; default height 0 makes the first-frame guard skip the draw.
        private Rect _scrollViewRect = new Rect(0f, 0f, 0f, 0f);

        // W14: last Repaint's shaded-view toggle row rect (captured at the top of
        // DrawChannelPanes, immediately after the row's EndHorizontal) — the
        // content-width detector for the horizontal scrollbar in the pane band
        // math. Default width 0 keeps the detector quiet until the first Repaint.
        private Rect _channelToggleRowRect = new Rect(0f, 0f, 0f, 0f);

        // W14 gap fix: zero-MARGIN copy of EditorStyles.toggle for the pane-slot
        // reservations (lazily created — EditorStyles needs the editor skin).
        // Measurement is untouched (same content, same padding), so the pane
        // columns stay aligned with the toggle row; only the style-owned vertical
        // margin stacking is removed.
        private GUIStyle _channelPaneSlotStyle;

        private bool _dirty;
        private double _lastChange;
        private bool _busy;
        private bool _recomputing;
        private ColorSpace _previewColorSpace = ColorSpace.Uninitialized;

        // 05-03: set by RecomputePreview from the composed stylization context —
        // drives the Stylization section's Smoothing-skipped Info HelpBox (OQ-3).
        private bool _stylizeSmoothingSkipped;

        // 05.1 plan 03: set only inside RunStylePostFit — ORs into the stylize lens
        // for one synchronous recompute so the fit reads the palette-mapped stylized
        // output REGARDLESS of the preview lens (D-03 exemplar contract). Always
        // reset in a finally; the preview pane honestly keeps showing the stylized
        // result that was just fitted. 05.2 plan 04 (D-04) / 05.3: the forced pass
        // is composed with the window's ATTACHED merged Style Asset — references
        // and stylization both come from that one asset (STYL-15).
        internal bool _forceStylizeForFit;

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
            NamerStyleAsset.Changed += OnStyleAssetChanged;

            // Seed-only-when-null: a fresh window opens on the current editor pick,
            // but a persisted pick (domain reload) is never clobbered. Unity's
            // overloaded null-comparison makes a destroyed/stale serialized
            // reference read as null, so the guard re-seeds in that case too.
            if (_selection == null)
            {
                _selection = ResolveSelection();
            }

            RebuildInspection();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            NamerStyleAsset.Changed -= OnStyleAssetChanged;

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

            // 05.1 T-05.1-04-02: the post-preview curve-strip cache must not leak
            // across lens flips / domain reloads.
            if (_postCurveTexture != null)
            {
                DestroyImmediate(_postCurveTexture);
                _postCurveTexture = null;
            }

            _postCurveTextureKey = null;

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

        /// <summary>
        /// 05.3 (05-03 donor): any edit to the ATTACHED merged Style Asset (the
        /// asset editor's sliders/curves raise
        /// <see cref="NamerStyleAsset.Changed"/>) re-marks the preview dirty on
        /// the standard debounce — the asset applies on load AND on change
        /// (D-06), with no focus change required.
        /// </summary>
        private void OnStyleAssetChanged(NamerStyleAsset asset)
        {
            _afterPanelState.MarkTweaking();
            MarkDirty();
        }

        private void RebuildInspection()
        {
            _model = SourceInspector.Inspect(_selection);

            _aoUnmultiplyStrength = _settings.AoUnmultiplyStrength;
            _aoBlurRadius = _settings.AoBlurRadius;
            _aoStrength = _settings.AoStrength;
            _aoContrast = _settings.AoContrast;
            _afterView = _settings.AfterView;
            _decompositionEnabled = _settings.DecompositionEnabled;
            _roughnessStageEnabled = _settings.RoughnessStageEnabled;
            _aoStageEnabled = _settings.AoStageEnabled;
            _metallicContributionEnabled = _settings.MetallicContributionEnabled;
            _emissiveContributionEnabled = _settings.EmissiveContributionEnabled;
            _errorThreshold = _settings.ErrorThreshold;
            _coverageTarget = _settings.CoverageTarget;
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
            ApplyPreviewPostLook();
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

                // 2026-09-21 contract: the AO stage checkbox gates synthesis only (bake
                // vs white fill, in the D-07 gate) — never the authored-map transfer —
                // so the un-multiply strength passes through unchanged (white surface B
                // makes the divide an identity whatever the strength).
                inspection.AoUnmultiplyStrength = _aoUnmultiplyStrength;
                inspection.AoStageEnabled = _aoStageEnabled;
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

                // 05-03 W2/W4 donor / 05.3 (STYL-15): the attached merged Style
                // Asset + the preview lens (mirrors the NamerProcessor composition;
                // the lens only gates the PREVIEW — Process always bakes with the
                // asset). 05.1 plan 03: a forced fit pass (_forceStylizeForFit, set
                // only inside RunStylePostFit) ORs into the lens so the fit reads
                // the stylized output regardless of the popup. 05.2 plan 04 (D-04):
                // the forced pass is composed with the window's ATTACHED asset —
                // the references come from the same merged asset being fitted, so
                // there is no per-fit override anymore (the WR-02 job is vacuous
                // when the stylization asset is always the attached one).
                NamerStyleAsset styleAsset = NamerProcessor.ResolveStyleAsset(_settings);
                bool stylizeLens = styleAsset != null
                    && (_afterView == kAfterViewStylized || _afterView == kAfterViewPost || _forceStylizeForFit);

                // 2026-09-21 contract follow-up: prime the bake mesh for the AO stage too,
                // so AO on + decomposition off previews the bake Process actually generates
                // (not the white fill). 05-03: smoothing > 0 joins the disjunct — island
                // ids need the split mesh. The guard reason still suppresses priming —
                // Process nulls the mesh for CR-01-guarded selections, so the preview must
                // match.
                bool primeBakeMesh = (decompWillRun || _aoStageEnabled
                    || NamerProcessor.StylizationNeedsMesh(styleAsset)) && string.IsNullOrEmpty(_decompGuardReason);

                inspection.BakeSourceMesh = primeBakeMesh ? _previewMesh : null;
                int baseW = inspection.BaseMap != null ? inspection.BaseMap.width : NamerComputePipeline.DefaultBaseResolution;
                int baseH = inspection.BaseMap != null ? inspection.BaseMap.height : NamerComputePipeline.DefaultBaseResolution;

                // 04.2 projection preview wiring: when decomposition will run AND the dip
                // source is Removed Detail, build the projection context exactly like
                // NamerProcessor (its internal CreateProjectionContext) so the preview shows
                // the projection + transfer Process actually generates. Otherwise pass null
                // (Sobel fallback, or Phase-3 when decomposition is off / guard-skipped).
                // 05-03: when decomposition runs, its fitSplit is reused as the stylize
                // split; when only stylization needs the mesh, split once here (lens only —
                // no cost when the popup reads NAMER).
                _projectionContext = null;
                NamerSplitResult stylizeSplit = null;
                if (decompWillRun && _previewMesh != null
                    && (_dipSource == NamerDipSource.RemovedDetail || stylizeLens))
                {
                    EnsureDecompPipeline();
                    NamerSplitResult fitSplit = MeshVertexSplitter.Split(_previewMesh);
                    if (_dipSource == NamerDipSource.RemovedDetail)
                    {
                        _projectionContext = NamerProcessor.CreateProjectionContext(
                            fitSplit, _decompPipeline, baseW, baseH, _errorThreshold, _residualResolution, _writeResidual,
                            _coverageTarget);
                    }

                    if (stylizeLens)
                    {
                        stylizeSplit = fitSplit;
                    }
                }
                else if (stylizeLens && _previewMesh != null)
                {
                    stylizeSplit = MeshVertexSplitter.Split(_previewMesh);
                }

                // 05.2-03 (STYL-11): the palette transfer never pulls luma, so the
                // fit pass reads the same stylized base as the Stylized lens — the
                // solver fits the FULL tonal distance into the post params.
                NamerStyleStylization stylization = ComposePreviewStylization(
                    styleAsset, _forceStylizeForFit ? kAfterViewStylized : _afterView, stylizeSplit);
                _stylizeSmoothingSkipped = stylization != null && stylization.SmoothingSkippedNoMesh;
                NamerProcessor.ApplyStyleControls(inspection, stylization != null ? styleAsset : null);

                _liveResult = _pipeline.Process(inspection, _projectionContext, stylization);

                RenderTexture previewBaseMap = ResolvePreviewBaseMap();

                // Stylize albedo preview source (Task 6): the styled SOURCE base
                // (pre-projection), not the projected/packed texture the After material
                // samples. Gamma projects need the same re-encode as the before pane;
                // Linear projects bind the pool target directly (no work here — and never
                // a Blit in OnGUI).
                if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
                {
                    Texture stylizeBase = ResolveStylizePreviewBase(_liveResult);
                    ReencodeForGammaPreview((RenderTexture)stylizeBase, ref _stylizePreviewBaseRt);
                }

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
                // ao-unmultiply-roundtrip (contract C): the After pane must re-multiply
                // by the same pack-time strength _pipeline.Process just divided with
                // (set at the top of RecomputePreview), matching the generated .mat.
                _namerMaterial.SetFloat(AoUnmultiplyStrengthId, inspection.AoUnmultiplyStrength);

                if (inspection.Emissive > 0f)
                {
                    _namerMaterial.EnableKeyword("_EMISSION");
                }
                else
                {
                    _namerMaterial.DisableKeyword("_EMISSION");
                }

                ApplyDebugGates();
                ApplyPreviewPostLook();

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
        /// 05.1 D-04 (STYL-09) / 05.2 plan 04 (W10, D-08): pushes the SELECTED
        /// post-look asset's fitted grade onto the live preview material and (when
        /// present) the generated After material, following every
        /// <see cref="ApplyDebugGates"/> call site. The Stylized + Post lens ONLY —
        /// the other two After Views write the neutral table, so switching lenses
        /// restores the identity grade (in-shader default).
        /// </summary>
        private void ApplyPreviewPostLook()
        {
            NamerStyleAsset asset = NamerProcessor.ResolveStyleAsset(_settings);
            bool postActive = _afterView == kAfterViewPost;
            ApplyPostLook(_namerMaterial, asset, postActive);
            ApplyPostLook(_generatedMaterial, asset, postActive);

            // Pane bloom rides the same funnel: armed only when the post lens is
            // active AND the attached merged asset carries a fit (the W16 degrade
            // falls back to plain Stylized NAMER — no bloom either). 05.2-03 (R7) /
            // 05.2 plan 04: the intensity applies at FULL STRENGTH, read from the
            // asset's PostBloomIntensity artist knob (A9) — no scaling path exists;
            // the renderer's enabled gate (intensity > 0 AND threshold > 0) keeps
            // Render byte-identical to the disabled path.
            bool fitted = postActive && asset != null && asset.HasFit;
            _preview.SetPostBloom(
                BloomThresholdLinear(asset),
                fitted ? asset.PostBloomIntensity : 0f);
        }

        /// <summary>
        /// 05.1 D-04 / 05.2 plan 04 (W10): the bloom threshold in LINEAR space — the
        /// exact conversion URP's PostProcessPass applies at render time to the
        /// gamma-space slider value. A null or unfitted asset returns 0 (the
        /// disabled sentinel).
        /// </summary>
        internal static float BloomThresholdLinear(NamerStyleAsset asset)
        {
            if (asset == null || !asset.HasFit)
            {
                return 0f;
            }

            return Mathf.GammaToLinearSpace(asset.PostBloomThreshold);
        }

        /// <summary>
        /// Writes the FULL post-preview uniform table onto one material — the
        /// ApplyDebugGates unconditional-binding discipline (Metal silently skips
        /// dispatches when a referenced property is unbound). Even the neutral/
        /// inactive case writes every uniform: _PostEnable 0 (shader-side identity
        /// early-out) with identity LMS/EV/z/s and a null curve strip. Active case
        /// derives the CPU-side twins exactly like URP's own passes:
        /// ColorUtils.ColorBalanceToLMSCoeffs (the function ColorGradingLutPass
        /// calls) and z/s = value/100 + 1. 05.2-03 (R7) / 05.2 plan 04 (W10): the
        /// selected asset's fitted values apply at FULL STRENGTH — direct unscaled
        /// look-field reads, no scaling path exists — while the neutral/
        /// unconditional-binding discipline is unchanged.
        /// </summary>
        internal static void ApplyPostLook(Material material, NamerStyleAsset asset, bool postActive)
        {
            if (material == null)
            {
                return;
            }

            bool enable = postActive && asset != null && asset.HasFit;
            material.SetFloat(PostEnableId, enable ? 1f : 0f);
            material.SetVector(
                PostLmsCoeffsId,
                asset != null
                    ? (Vector4)UnityEngine.Rendering.ColorUtils.ColorBalanceToLMSCoeffs(
                        asset.PostTemperature,
                        asset.PostTint)
                    : Vector4.one);
            material.SetFloat(
                PostExposureEvId,
                asset != null ? asset.PostExposureEv : 0f);
            material.SetFloat(
                PostContrastZId,
                asset != null ? asset.PostContrast / 100f + 1f : 1f);
            material.SetFloat(
                PostSaturationSId,
                asset != null ? asset.PostSaturation / 100f + 1f : 1f);
            material.SetTexture(PostCurveTexId, enable ? GetOrCreatePostCurveTexture(asset) : null);
        }

        /// <summary>
        /// One-slot static cache for the 128x1 curve strip: rebuilt only when the
        /// selected post-look asset's identity or any curve key changes (a re-fit
        /// can land on the same fingerprint while editing keys, so the key dump —
        /// not the fingerprint — is the cache key). Released in
        /// <see cref="OnDisable"/>.
        /// </summary>
        private static Texture2D GetOrCreatePostCurveTexture(NamerStyleAsset asset)
        {
            string key = ComputePostCurveCacheKey(asset);
            if (_postCurveTexture != null && _postCurveTextureKey == key)
            {
                return _postCurveTexture;
            }

            if (_postCurveTexture != null)
            {
                DestroyImmediate(_postCurveTexture);
            }

            _postCurveTexture = BuildPostCurveTexture(asset);
            _postCurveTextureKey = key;
            return _postCurveTexture;
        }

        /// <summary>
        /// Cache identity: the Style Asset's instance ID plus every key (time,
        /// value) of all four fitted curves, invariant-culture formatted. Null
        /// curves contribute nothing (the texture builder falls back to the
        /// diagonal).
        /// </summary>
        private static string ComputePostCurveCacheKey(NamerStyleAsset asset)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(asset.GetInstanceID());
            AppendCurveKey(builder, "M", asset.PostMasterCurve);
            AppendCurveKey(builder, "R", asset.PostRedCurve);
            AppendCurveKey(builder, "G", asset.PostGreenCurve);
            AppendCurveKey(builder, "B", asset.PostBlueCurve);
            return builder.ToString();
        }

        /// <summary>
        /// Appends one curve's tag + keys to the cache-key builder. Empty curves
        /// append just the tag so a null curve and an empty curve stay distinct
        /// from a curve with keys.
        /// </summary>
        private static void AppendCurveKey(StringBuilder builder, string tag, AnimationCurve curve)
        {
            builder.Append('|');
            builder.Append(tag);
            if (curve == null)
            {
                return;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                builder.Append(':');
                builder.Append(keys[i].time.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(keys[i].value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// Builds the 128x1 RGBA float strip the shader samples as the YRGB curve
        /// bank: .r/.g/.b are the fitted R/G/B curves, .a the master curve — the
        /// same packing URP's LUT-builder curve baker uses. 05.2-03 (R7): the four
        /// fitted curves are evaluated DIRECTLY at full strength — no scaling
        /// path exists. Linear filter + clamp wrap so in-shader bilinear fetches
        /// land between texels exactly like the LUT builder's LinearClamp
        /// sampling of its curve textures.
        /// </summary>
        private static Texture2D BuildPostCurveTexture(NamerStyleAsset asset)
        {
            AnimationCurve masterCurve = asset.PostMasterCurve;
            AnimationCurve redCurve = asset.PostRedCurve;
            AnimationCurve greenCurve = asset.PostGreenCurve;
            AnimationCurve blueCurve = asset.PostBlueCurve;
            Color[] pixels = new Color[kPostCurveTexelCount];
            for (int i = 0; i < kPostCurveTexelCount; i++)
            {
                float t = (float)i / (kPostCurveTexelCount - 1);
                pixels[i] = new Color(
                    EvaluatePostCurveChannel(redCurve, t),
                    EvaluatePostCurveChannel(greenCurve, t),
                    EvaluatePostCurveChannel(blueCurve, t),
                    EvaluatePostCurveChannel(masterCurve, t));
            }

            Texture2D strip = new Texture2D(kPostCurveTexelCount, 1, TextureFormat.RGBAFloat, false, true)
            {
                name = "NamerPostCurveStrip",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            strip.SetPixels(pixels);
            strip.Apply(false, false);
            return strip;
        }

        /// <summary>
        /// Evaluates one fitted curve channel at t. A null curve falls back to the
        /// identity diagonal (t) so a hand-broken asset still previews a defined
        /// grade; values are clamped to [0,1] like the shader's saturate.
        /// </summary>
        private static float EvaluatePostCurveChannel(AnimationCurve curve, float t)
        {
            if (curve == null)
            {
                return t;
            }

            return Mathf.Clamp01(curve.Evaluate(t));
        }

        /// <summary>
        /// Resolves the albedo-preview caption row for the current After View
        /// (05.1 D-04). The un-stylized view draws the Before/After flipper; both
        /// stylized lenses replace it with their locked caption.
        /// </summary>
        internal static string ResolveAlbedoPreviewCaption(int afterView, out bool drawFlipper)
        {
            if (afterView == kAfterViewPost)
            {
                drawFlipper = false;
                return AlbedoPreviewPostCaption;
            }

            if (afterView == kAfterViewStylized)
            {
                drawFlipper = false;
                return AlbedoPreviewStylizedCaption;
            }

            drawFlipper = true;
            return AlbedoPreviewUnstylizedCaption;
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

            return ReencodeForGammaPreview(_liveResult.NormalizedBaseColor, ref _previewBaseRt);
        }

        /// <summary>
        /// Re-encodes a linear float16 pipeline target into the sRGB 8-bit
        /// <paramref name="cache"/> for Gamma-project preview sampling — shared by the
        /// before-pane base map (<see cref="ResolvePreviewBaseMap"/>) and the stylize
        /// albedo preview's source base. A Gamma project performs no decode anywhere, so
        /// the raw linear values would shade darker than the before pane (and darker than
        /// the correctly saved material). The blit runs through the raw-copy material with
        /// <c>_REENCODE_SRGB</c> — the same exact IEC 61966-2-1 primitive the save path
        /// uses — so the previews match the saved assets in both color spaces. The cache
        /// is sized to the live result and released/recreated on size change. NEVER call
        /// from OnGUI (Graphics.Blit) — RecomputePreview only.
        /// </summary>
        private RenderTexture ReencodeForGammaPreview(RenderTexture source, ref RenderTexture cache)
        {
            if (cache == null
                || cache.width != _liveResult.Width
                || cache.height != _liveResult.Height)
            {
                if (cache != null)
                {
                    cache.Release();
                    DestroyImmediate(cache);
                }

                var descriptor = new RenderTextureDescriptor(
                    _liveResult.Width, _liveResult.Height, GraphicsFormat.R8G8B8A8_UNorm, 0)
                {
                    sRGB = false,
                };
                cache = new RenderTexture(descriptor)
                {
                    // Same sweep protection as the pool: without DontSave the editor's
                    // unused-asset sweeps destroy this RT between repaints.
                    hideFlags = HideFlags.HideAndDontSave,
                };
                cache.Create();
            }

            Material rawCopy = NamerComputePipeline.RawCopyMaterial();
            rawCopy.EnableKeyword(NamerComputePipeline.ReencodeSrgbKeyword);
            Graphics.Blit(source, cache, rawCopy);
            rawCopy.DisableKeyword(NamerComputePipeline.ReencodeSrgbKeyword);
            return cache;
        }

        private void ReleaseLiveResult()
        {
            if (_previewBaseRt != null)
            {
                _previewBaseRt.Release();
                DestroyImmediate(_previewBaseRt);
                _previewBaseRt = null;
            }

            if (_stylizePreviewBaseRt != null)
            {
                _stylizePreviewBaseRt.Release();
                DestroyImmediate(_stylizePreviewBaseRt);
                _stylizePreviewBaseRt = null;
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
                    mode: _writeResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep,
                    coverageTarget: _coverageTarget);
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
            DrawStylizationSection(inspection);
            DrawDecompositionSection(inspection);
            DrawOutputSection();
            EditorGUILayout.EndScrollView();
            // Widget rect captured AFTER EndScrollView feeds the visible-band math in DrawAlbedoPreview; rects INSIDE the scroll view are content-space, so the band is expressed in the same coords as the row rect.
            // GetLastRect() returns a degenerate 1x1 rect during Layout events (probe-verified 2026-09-26), so capture only on Repaint; an unconditional capture would hand every Repaint pass the Layout pass's rect and the band math would collapse (preview invisible).
            if (Event.current.type == EventType.Repaint)
            {
                _scrollViewRect = GUILayoutUtility.GetLastRect();
            }

            DrawActionSection(inspection);
        }

        /// <summary>
        /// DIP-01 hard stage dip-switch row rendered above the foldouts. VC + Residual reuses
        /// the existing <see cref="NamerProcessorSettings.DecompositionEnabled"/> setting;
        /// Roughness/AO hard-gate the matching pipeline stages independently of their strength
        /// sliders; Metallic/Emissive gate the shader only (no pipeline stage exists). Each
        /// toggle writes through to its persisted setting. The five toggles pin to the debug
        /// strip's <see cref="kDebugColumnWidth"/> + <see cref="kDebugColumnGap"/> grid
        /// (columns 0-4, own automatic rects).
        /// </summary>
        private void DrawStepSwitches()
        {
            EditorGUILayout.LabelField("Stage & Shader Gates", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("VC + Residual, Roughness, and AO gate pipeline stages — applied on the next Process run. Metallic and Emissive gate the shader only (live, no re-Process needed).", EditorStyles.miniLabel);
            EditorGUI.BeginDisabledGroup(_busy);
            EditorGUILayout.BeginHorizontal();

            bool newDecomposition = EditorGUILayout.ToggleLeft(
                new GUIContent("VC + Residual",
                    "Hard stage gate for vertex-color decomposition + residual (reuses DecompositionEnabled). Independent of the strength sliders."),
                _decompositionEnabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newDecomposition != _decompositionEnabled)
            {
                _decompositionEnabled = newDecomposition;
                _settings.DecompositionEnabled = newDecomposition;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            GUILayout.Space(kDebugColumnGap);

            bool newRoughness = EditorGUILayout.ToggleLeft(
                new GUIContent("Roughness",
                    "Hard stage gate for roughness extraction — skips extraction without touching the strength slider."),
                _roughnessStageEnabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newRoughness != _roughnessStageEnabled)
            {
                _roughnessStageEnabled = newRoughness;
                _settings.RoughnessStageEnabled = newRoughness;
                MarkDirty();
            }

            GUILayout.Space(kDebugColumnGap);

            bool newAo = EditorGUILayout.ToggleLeft(
                new GUIContent("AO",
                    "Hard stage gate for the AO stage: OFF packs surface B white (no synthetic AO); ON packs the AO map — an authored _OcclusionMap always transfers either way, and without one the geometry bake is the synthetic source. The un-multiply strength slider is independent of this gate."),
                _aoStageEnabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newAo != _aoStageEnabled)
            {
                _aoStageEnabled = newAo;
                _settings.AoStageEnabled = newAo;
                MarkDirty();
            }

            GUILayout.Space(kDebugColumnGap);

            bool newMetallic = EditorGUILayout.ToggleLeft(
                new GUIContent("Metallic",
                    "Shader-only gate for the metallic contribution (no pipeline stage exists)."),
                _metallicContributionEnabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newMetallic != _metallicContributionEnabled)
            {
                _metallicContributionEnabled = newMetallic;
                _settings.MetallicContributionEnabled = newMetallic;
                ApplyDebugGates();
                ApplyPreviewPostLook();
                Repaint();
            }

            GUILayout.Space(kDebugColumnGap);

            bool newEmissive = EditorGUILayout.ToggleLeft(
                new GUIContent("Emissive",
                    "Shader-only gate for the emissive contribution (no pipeline stage exists)."),
                _emissiveContributionEnabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newEmissive != _emissiveContributionEnabled)
            {
                _emissiveContributionEnabled = newEmissive;
                _settings.EmissiveContributionEnabled = newEmissive;
                ApplyDebugGates();
                ApplyPreviewPostLook();
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

            EditorGUI.BeginDisabledGroup(_busy);
            UnityEngine.Object newSelection = EditorGUILayout.ObjectField(
                new GUIContent(
                    "Source",
                    "The object to process. This pick is explicit — it does NOT follow the editor selection; "
                        + "clicking other items or empty space leaves it unchanged. Assign a GameObject, prefab, "
                        + "model, material, or folder to rebuild the inspection immediately."),
                _selection,
                typeof(UnityEngine.Object),
                true);
            if (newSelection != _selection)
            {
                _selection = newSelection;
                RebuildInspection();
            }

            EditorGUI.EndDisabledGroup();

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

            // 05-03 W4: preview-only lens above the Before/After caption row. NEVER
            // gates Process — the bake always applies the attached Style Asset; only
            // the preview's computation swaps between the un-stylized and stylized bases.
            // 05.1 W5: the third entry (Stylized + Post) keeps the stylized base and
            // grades the After pane in-shader (ApplyPreviewPostLook + pane bloom).
            NamerStyleAsset lensAsset = NamerProcessor.ResolveStyleAsset(_settings);
            EditorGUI.BeginDisabledGroup(_busy || lensAsset == null);
            int newAfterView = EditorGUILayout.Popup(
                new GUIContent(
                    "After View",
                    "Preview-only lens: NAMER computes the un-stylized packed result; Stylized NAMER "
                        + "applies the attached Style Asset (palette transfer, smoothing, and the "
                        + "asset's bake-side controls). Stylized NAMER applies palette transfer, which "
                        + "never touches tonality — the source keeps its own luma (expected to look "
                        + "flatter than a graded render). Stylized + Post additionally applies the "
                        + "attached Style Asset's fitted post look (white balance, exposure, "
                        + "contrast, saturation, curves, bloom) in-shader, matching the asset's "
                        + "generated scene Volume so the look is approved without play mode. Process "
                        + "always bakes with the attached Style Asset "
                        + "regardless of this popup. Recomputes the preview in memory "
                        + NamerEditorConstants.DebounceSeconds + " s after the change — nothing is written to disk."),
                _afterView,
                AfterViewOptions);
            if (newAfterView != _afterView)
            {
                _afterView = newAfterView;
                _settings.AfterView = newAfterView;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            if (lensAsset == null)
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetAttachHint,
                    EditorStyles.miniLabel);
            }

            EditorGUI.EndDisabledGroup();

            // 05.3 W16 (D-08 donor): the stylized lenses are honest about their
            // degrade — an attached but NOT DECOMPOSED merged asset has neither a
            // palette nor a fitted post look, so both stylized views fall back to
            // the NAMER preview (ApplyPostLook's enable gate + the null
            // stylization). The 5.2 two-variant fallback (no asset / no fit)
            // collapses into this single Info; the no-asset state is carried by
            // the W18 popup-disabled hint above.
            if (lensAsset != null && !lensAsset.IsDecomposed
                && (_afterView == kAfterViewStylized || _afterView == kAfterViewPost))
            {
                EditorGUILayout.HelpBox(
                    string.Format(
                        NamerEditorConstants.StyleAssetNotDecomposedFallbackFormat,
                        lensAsset.name),
                    MessageType.Info);
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
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(1, ref _dbgRoughnessEnabled);
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(2, ref _dbgAoEnabled);
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(3, ref _dbgMetallicEnabled);
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(4, ref _dbgEmissiveEnabled);
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(5, ref _dbgVertexColorEnabled);
            GUILayout.Space(kDebugColumnGap);
            DrawShaderInputToggle(6, ref _dbgNormalEnabled);
            EditorGUILayout.EndHorizontal();
            if (Event.current.type == EventType.Repaint)
            {
                // Capture the single toggle row as the strip's overflow detector
                // input — DrawChannelPanes consumes it for the pane clip band.
                // Repaint-gated GetLastRect-after-EndHorizontal (degenerate during
                // Layout); reused on other event types.
                _channelToggleRowRect = GUILayoutUtility.GetLastRect();
            }

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
            // 2026-09-28 amendment: the checkbox and label keep their OWN automatic
            // rect calculation — EditorGUILayout.ToggleLeft sizes its own
            // checkbox+label slot with default styles (the padding-zeroed style and
            // manual CalcHeight slot are NOT restored) — but the slot is pinned to
            // the kDebugColumnWidth grid column so it stays above its pane.
            // kDebugColumnGap spacing lives in the caller.
            bool newValue = EditorGUILayout.ToggleLeft(
                new GUIContent(ShaderInputToggleLabels[index],
                    "Neutralizes the '" + ShaderInputToggleLabels[index]
                        + "' input in the full shaded After view (shader-only, no GPU recompute)."),
                enabled,
                GUILayout.Width(kDebugColumnWidth));
            if (newValue != enabled)
            {
                enabled = newValue;
                ApplyDebugGates();
                ApplyPreviewPostLook();
                Repaint();
            }
        }

        /// <summary>
        /// Lazily creates the zero-margin/zero-padding style used by the channel
        /// pane row slots (lazily — EditorStyles needs the editor skin).
        /// </summary>
        private void EnsureChannelStripStyles()
        {
            if (_channelPaneSlotStyle == null)
            {
                _channelPaneSlotStyle = new GUIStyle(EditorStyles.toggle)
                {
                    margin = new RectOffset(0, 0, 0, 0),
                    padding = new RectOffset(0, 0, 0, 0)
                };
            }
        }

        /// <summary>
        /// D-12 channel pane row: six small panes in ONE row directly under the
        /// matching shaded-view toggle row, each drawn through the
        /// <c>NamerChannelView</c> material so the pane shows
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

            // W14 gap fix: the pane slots use the zero-margin/zero-padding style
            // from EnsureChannelStripStyles so no style-owned vertical margin
            // stacks between the toggle row and the pane row.
            EnsureChannelStripStyles();

            // W14 clip: the panes are CONTENT-space rects inside the scroll view and
            // Graphics.DrawTexture does not clip to it (the mtv 260926-mtv defect
            // class) — derive the visible band from the captured widget rect
            // exactly like DrawAlbedoPreview. _channelToggleRowRect is captured at
            // the toggle-row site (the single strip row) because GetLastRect here
            // would read the pane row and miss toggle-driven overflow.
            float vbarW = GUI.skin.verticalScrollbar.fixedWidth;
            float hbarH = GUI.skin.horizontalScrollbar.fixedHeight;
            // The vertical scrollbar is always shown in this window (the foldout
            // content is always far taller than the view), so the subtract is
            // unconditional by design.
            float viewWidth = _scrollViewRect.width - vbarW;
            // Exact overflow detector: the toggle row above pins at exactly
            // viewWidth when the content fits and stretches past it on overflow
            // (the probe-verified expand-row law); 0.5f is a float-noise epsilon.
            bool hbarShown = _channelToggleRowRect.width > viewWidth + 0.5f;
            float viewHeight = _scrollViewRect.height - (hbarShown ? hbarH : 0f);
            Rect visibleBand = new Rect(_scrollPosition.x, _scrollPosition.y, viewWidth, viewHeight);

            EditorGUILayout.BeginHorizontal();
            for (int toggle = 0; toggle < ShaderInputToggleLabels.Length; toggle++)
            {
                if (toggle > 0)
                {
                    // Same fixed gap as the toggle row so pane columns stay under
                    // their own toggles in the uniform 7-column grid.
                    GUILayout.Space(kDebugColumnGap);
                }

                // One uniform kDebugColumnWidth slot per toggle column — the pane
                // texture itself stays ChannelPaneSize, horizontally centered in
                // its column.
                Rect slot = GUILayoutUtility.GetRect(GUIContent.none, _channelPaneSlotStyle,
                    GUILayout.Width(kDebugColumnWidth), GUILayout.Height(ChannelPaneSize));
                int channel = ChannelPaneForToggle[toggle];
                if (channel < 0)
                {
                    // No texture for this input (Vertex Color); slot is already
                    // reserved, nothing to draw.
                    continue;
                }

                Rect paneRect = new Rect(slot.x + (slot.width - ChannelPaneSize) * 0.5f, slot.y,
                    ChannelPaneSize, ChannelPaneSize);

                if (afterMaterial == null || afterMaterial.GetTexture(SurfaceMapId) == null)
                {
                    // No generated textures yet: neutral no-op box, no draw/tooltip/click.
                    GUI.Box(paneRect, GUIContent.none);
                    continue;
                }

                // W14 clip: intersect the pane with the visible band; a zero-area
                // clip (fully scrolled out / first frame before the Repaint
                // capture) skips the draw, label, cursor, AND hit-test — label,
                // cursor, and click rects all derive from the SAME clipped rect,
                // so no invisible interactive surface extends under the bar.
                Rect clippedRect = ComputeChannelPaneClip(paneRect, visibleBand, out Rect uvRect);
                if (clippedRect.width <= 0f || clippedRect.height <= 0f)
                {
                    continue;
                }

                EnsureChannelViewMaterial();
                _channelViewMaterial.SetTexture(SurfaceMapId, afterMaterial.GetTexture(SurfaceMapId));
                _channelViewMaterial.SetTexture(BaseResidualMapId, afterMaterial.GetTexture(BaseResidualMapId));
                _channelViewMaterial.SetFloat(OcclusionStrengthId, afterMaterial.GetFloat(OcclusionStrengthId));
                _channelViewMaterial.SetFloat(ChannelId, channel);
                if (Event.current.type == EventType.Repaint)
                {
                    // Per-draw _UvRect window (mtv): a partially clipped pane
                    // samples only its visible region — the clipped-off portion is
                    // removed, never stretched into the trimmed rect. Set
                    // immediately before the draw and reset to identity immediately
                    // after so later consumers of the shared material are unaffected.
                    _channelViewMaterial.SetVector(UvRectId, new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height));
                    Graphics.DrawTexture(clippedRect, Texture2D.whiteTexture, _channelViewMaterial);
                    _channelViewMaterial.SetVector(UvRectId, new Vector4(0f, 0f, 1f, 1f));
                }

                GUI.Label(clippedRect, new GUIContent(string.Empty,
                    ChannelPaneLabels[channel] + " — source: " + (channel == 0 ? "_BaseResidualMap" : "_SurfaceMap")));
                EditorGUIUtility.AddCursorRect(clippedRect, MouseCursor.Zoom);

                if (Event.current.type == EventType.MouseDown && clippedRect.Contains(Event.current.mousePosition))
                {
                    Event.current.Use();
                    ShowChannelPopup(clippedRect, channel, afterMaterial);
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

        /// <summary>
        /// 05-03 W1-W3: the Stylization section — profile attach (W2, GUID-persisted),
        /// the attach-state line (W3, honesty about what the preview uses), and the
        /// smoothing-skipped Info note (OQ-3). The profile reference itself is the
        /// opt-in gate (null = stylization stage off — no new Stage &amp; Shader Gates
        /// toggle), matching the DecompositionEnabled precedent.
        /// </summary>
        private void DrawStylizationSection(NamerMaterialInspection inspection)
        {
            _settings.FoldoutStylization = EditorGUILayout.Foldout(_settings.FoldoutStylization, "Stylization", true);
            if (!_settings.FoldoutStylization)
            {
                return;
            }

            EditorGUI.BeginDisabledGroup(_busy);

            // 05.3 W15 (D-06): the panel is load/use only — ONE Style Asset object
            // field, no Create button, no embedded inspector. Decomposing, fitting,
            // and tweaking live in the asset's own inspector; creation is menu-only
            // (Assets/Create/NAMER/Style). Attach/detach/swap route through the
            // donor row's GUID mechanics + debounced preview recompute.
            NamerStyleAsset attached = NamerProcessor.ResolveStyleAsset(_settings);
            NamerStyleAsset newAsset = (NamerStyleAsset)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Style Asset",
                    string.Format(NamerEditorConstants.StyleAssetFieldTooltipFormat, NamerEditorConstants.DebounceSeconds)),
                attached,
                typeof(NamerStyleAsset),
                false);
            if (newAsset != attached)
            {
                _settings.StyleAssetGuid = newAsset != null
                    ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(newAsset))
                    : string.Empty;
                attached = newAsset;
                _afterPanelState.MarkTweaking();
                MarkDirty();
            }

            if (attached != null)
            {
                EditorGUILayout.LabelField(DescribeStyleAssetState(attached), EditorStyles.miniLabel);
            }

            if (_stylizeSmoothingSkipped)
            {
                EditorGUILayout.HelpBox(
                    "Smoothing unavailable — no mesh to derive UV islands",
                    MessageType.Info);
            }

            if (attached != null)
            {
                DrawAlbedoPreview();
            }

            EditorGUI.EndDisabledGroup();
        }

        /// <summary>
        /// The 2D albedo preview (stylization fix 3, Task 6 corrected): a full-width
        /// Base-channel view of the styled SOURCE base (pre-projection — see
        /// <see cref="ResolveStylizePreviewBase"/>), never the post-processed texture the
        /// After material samples at <c>_BaseResidualMap</c> (the removed-detail residual,
        /// or the Gouraud-projected base). The packed surface map and occlusion strength
        /// still come from the live <c>_namerMaterial</c>. Drawn through the shared
        /// <c>NamerChannelView</c> material. Read-only view of the last preview recompute:
        /// no new GPU compute, no readback, no disk I/O. The caption row renders ALWAYS —
        /// including the neutral state (Task 7: the empty image no longer swallows the
        /// View Stylized lens flipper; it is disabled when no profile is attached, and a
        /// reason line names the missing prerequisite via
        /// <see cref="ResolveAlbedoPreviewEmptyReason"/>).
        /// </summary>
        private void DrawAlbedoPreview()
        {
            EditorGUILayout.LabelField("Albedo Preview", EditorStyles.boldLabel);

            // Expand-to-content-width reservation (not an absolute row width): the row can
            // never extend under the vertical scrollbar at narrow window widths.
            Rect rect = GUILayoutUtility.GetRect(0f, kAlbedoPreviewHeight, GUILayout.ExpandWidth(true), GUILayout.Height(kAlbedoPreviewHeight));

            // Gamma projects sample the re-encoded cache built in RecomputePreview; Linear
            // projects bind the pool target directly.
            Texture stylizeBase = QualitySettings.activeColorSpace == ColorSpace.Gamma
                ? _stylizePreviewBaseRt
                : ResolveStylizePreviewBase(_liveResult);
            Texture surfaceMap = _namerMaterial != null ? _namerMaterial.GetTexture(SurfaceMapId) : null;
            bool canDrawImage = surfaceMap != null && stylizeBase != null;

            // Same asset resolution the After View popup uses: without an attached
            // Style Asset the lens flip stays visible but disabled (mirrors the
            // popup's disable state).
            bool hasProfile = NamerProcessor.ResolveStyleAsset(_settings) != null;

            if (canDrawImage)
            {
                // Channel-view pattern (260919-fp3): set all four properties before the draw,
                // Repaint-guarded Graphics.DrawTexture — NEVER Graphics.Blit in OnGUI. Sharing
                // _channelViewMaterial with DrawChannelPanes is safe: both set all four
                // properties before every draw.
                EnsureChannelViewMaterial();
                _channelViewMaterial.SetTexture(SurfaceMapId, surfaceMap);
                _channelViewMaterial.SetTexture(BaseResidualMapId, stylizeBase);
                _channelViewMaterial.SetFloat(OcclusionStrengthId, _namerMaterial.GetFloat(OcclusionStrengthId));
                _channelViewMaterial.SetFloat(ChannelId, 0);

                // GUILayout rects inside the scroll view are CONTENT-space and the expand
                // row stretches to the CONTENT width (the widest fixed row), not the
                // visible panel width — center against the visible band and clip
                // explicitly because Graphics.DrawTexture does not clip to the scroll
                // view (it overdraws the horizontal scrollbar and the action header).
                if (_scrollViewRect.height > 0f)
                {
                    float vbarW = GUI.skin.verticalScrollbar.fixedWidth;
                    float hbarH = GUI.skin.horizontalScrollbar.fixedHeight;
                    // The vertical scrollbar is always shown in this window (the foldout
                    // content is always far taller than the view), so the subtract is
                    // unconditional by design.
                    float viewWidth = _scrollViewRect.width - vbarW;
                    // Exact overflow detector: an expand row pins at exactly viewWidth
                    // when the content fits and stretches to contentWidth past it
                    // (probe-verified); 0.5f is a float-noise epsilon.
                    bool hbarShown = rect.width > viewWidth + 0.5f;
                    float viewHeight = _scrollViewRect.height - (hbarShown ? hbarH : 0f);
                    ComputeAlbedoPreviewGeometry(rect, stylizeBase.width, stylizeBase.height, viewWidth, viewHeight, _scrollPosition, out Rect drawRect, out Rect uvRect);
                    if (drawRect.width > 0f && drawRect.height > 0f && Event.current.type == EventType.Repaint)
                    {
                        _channelViewMaterial.SetVector(UvRectId, new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height));
                        Graphics.DrawTexture(drawRect, Texture2D.whiteTexture, _channelViewMaterial);
                        // The material is shared with DrawChannelPanes (panes draw earlier
                        // in the frame than the preview); a stale _UvRect would corrupt
                        // pane draws next frame.
                        _channelViewMaterial.SetVector(UvRectId, new Vector4(0f, 0f, 1f, 1f));
                    }
                }
            }
            else
            {
                // Neutral box — but NEVER early-return past the caption row below (Task 7:
                // the empty state used to swallow the View Stylized button, leaving a dead
                // preview with no path back to the stylized lens).
                GUI.Box(rect, GUIContent.none);
            }

            // Caption row renders ALWAYS — lens honesty plus the preview-side lens flipper.
            // 05.1 W6: routed through ResolveAlbedoPreviewCaption so the post lens gets
            // its own locked caption (no flipper — the flipper only flips BETWEEN the
            // un-stylized and stylized bases).
            EditorGUILayout.BeginHorizontal();
            string caption = ResolveAlbedoPreviewCaption(_afterView, out bool drawFlipper);
            EditorGUILayout.LabelField(caption, EditorStyles.miniLabel);
            if (drawFlipper)
            {
                using (new EditorGUI.DisabledScope(!hasProfile))
                {
                    if (GUILayout.Button(AlbedoPreviewViewStylizedButton, EditorStyles.miniButton))
                    {
                        _afterView = kAfterViewStylized;
                        _settings.AfterView = kAfterViewStylized;
                        _afterPanelState.MarkTweaking();
                        MarkDirty();
                    }
                }
            }

            EditorGUILayout.EndHorizontal();

            if (!canDrawImage)
            {
                string reason = ResolveAlbedoPreviewEmptyReason(_selection != null, _liveResult != null);
                if (reason != null)
                {
                    EditorGUILayout.LabelField(reason, EditorStyles.miniLabel);
                }
            }
        }

        /// <summary>
        /// Pure view-band geometry for the albedo preview: aspect-fits the texture into
        /// the visible scroll band, centers on the VISIBLE width (the reserved row
        /// stretches to the scroll content width, not the panel width), clips the quad
        /// to the band, and reports the visible fraction as a UV sub-rect for the
        /// <c>_UvRect</c> shader window (texture-space; the v offset is measured from
        /// the quad bottom, matching the shader's bottom-origin window). Headless
        /// -testable, no GUI state.
        /// </summary>
        internal static void ComputeAlbedoPreviewGeometry(Rect rowRect, float textureWidth, float textureHeight, float viewWidth, float viewHeight, Vector2 scroll, out Rect drawRect, out Rect uvRect)
        {
            float fitScale = Mathf.Min(viewWidth / Mathf.Max(1f, textureWidth), rowRect.height / Mathf.Max(1f, textureHeight));
            float drawWidth = textureWidth * fitScale;
            float drawHeight = textureHeight * fitScale;

            // Unclipped fit rect: horizontal center on the VISIBLE band (scroll.x +
            // viewWidth/2 — NOT the content-width row center, which was the bug);
            // vertical centering within the row, unchanged.
            Rect fit = new Rect(
                scroll.x + (viewWidth - drawWidth) * 0.5f,
                rowRect.y + (rowRect.height - drawHeight) * 0.5f,
                drawWidth,
                drawHeight);

            Rect band = new Rect(scroll.x, scroll.y, viewWidth, viewHeight);
            Rect clipped = Rect.MinMaxRect(
                Mathf.Max(fit.xMin, band.xMin),
                Mathf.Max(fit.yMin, band.yMin),
                Mathf.Min(fit.xMax, band.xMax),
                Mathf.Min(fit.yMax, band.yMax));

            // Visible fraction of the UNCLIPPED fit rect (guarded denominators:
            // degenerate fits must not divide by zero). _UvRect windows in TEXTURE
            // space where v == 0 is the quad's BOTTOM edge (probe-verified
            // 2026-09-26: window (0,0,1,0.5) shows the texture's bottom half), so
            // the v offset is measured from the quad bottom, not the GUI top —
            // a top-based offset would sample the mirrored slice when clipped.
            float fitW = Mathf.Max(1f, fit.width);
            float fitH = Mathf.Max(1f, fit.height);
            uvRect = new Rect(
                (clipped.xMin - fit.xMin) / fitW,
                1f - (clipped.yMax - fit.yMin) / fitH,
                clipped.width / fitW,
                clipped.height / fitH);

            // A fully-out-of-band row yields an inverted/empty rect (width or
            // height <= 0) — the caller's degenerate check skips the draw.
            drawRect = clipped;
        }

        /// <summary>
        /// Pure band-intersection clip for one channel pane (W14 — the mtv
        /// 260926-mtv defect class): intersects <paramref name="paneRect"/> with
        /// the scroll view's visible band (content-space, derived from the
        /// captured widget rect exactly like <see cref="DrawAlbedoPreview"/>'s
        /// band) and reports the visible fraction as a UV sub-rect for the
        /// <c>_UvRect</c> shader window so the clipped-off region is REMOVED from
        /// sampling, never stretched. A pane fully outside the band — or a
        /// degenerate band (the first-frame default) — returns a zero-area rect
        /// so the caller skips the draw, label, cursor, and hit-test entirely.
        /// Headless-testable, no GUI state.
        /// </summary>
        internal static Rect ComputeChannelPaneClip(Rect paneRect, Rect visibleBand, out Rect uvRect)
        {
            Rect clipped = Rect.MinMaxRect(
                Mathf.Max(paneRect.xMin, visibleBand.xMin),
                Mathf.Max(paneRect.yMin, visibleBand.yMin),
                Mathf.Min(paneRect.xMax, visibleBand.xMax),
                Mathf.Min(paneRect.yMax, visibleBand.yMax));

            if (clipped.xMin >= clipped.xMax || clipped.yMin >= clipped.yMax)
            {
                // No overlap (or a degenerate band): zero-area, not an inverted
                // rect — the caller's width/height check skips everything, so no
                // pane pixels render under or into the horizontal scrollbar.
                uvRect = Rect.zero;
                return Rect.zero;
            }

            // Visible fraction of the pane (guarded denominators: a zero-size
            // pane must not divide by zero). _UvRect windows in TEXTURE space
            // where v == 0 is the quad's BOTTOM edge (probe-verified 2026-09-26),
            // so the v offset is measured from the quad bottom, not the GUI top —
            // a top-based offset would sample the mirrored slice when the pane
            // is clipped at the band bottom (the mtv bottom-origin lesson).
            float paneW = Mathf.Max(1f, paneRect.width);
            float paneH = Mathf.Max(1f, paneRect.height);
            uvRect = new Rect(
                (clipped.xMin - paneRect.xMin) / paneW,
                1f - (clipped.yMax - paneRect.yMin) / paneH,
                clipped.width / paneW,
                clipped.height / paneH);
            return clipped;
        }

        /// <summary>
        /// True when this window carries a loadable exemplar for Fit Look (05.1 D-03):
        /// a picked Source whose inspection resolved at least one material. The profile
        /// inspector's exemplar resolution (host window first, then any open window
        /// with a loadable source) keys off this — one definition of "loadable".
        /// </summary>
        internal bool HasLoadableSource
        {
            get { return _selection != null && PrimaryInspection != null; }
        }

        /// <summary>
        /// 05.1 plan 03 fit seam (D-01 explicit / D-03 exemplar) / 05.2 plan 04
        /// (D-04 asset-driven): renders the palette-mapped stylized output of the
        /// object currently loaded in this window REGARDLESS of the preview lens —
        /// <see cref="_forceStylizeForFit"/> ORs into the stylize lens for one
        /// synchronous recompute (the RequestBake explicit-action precedent — never
        /// EditorApplication.delayCall) — reads the live result's styled base back
        /// WITHOUT releasing it (the window keeps managing
        /// <see cref="_liveResult"/>), accumulates the merged asset's shared
        /// reference list in slot order with its weights, and runs the
        /// deterministic <see cref="NamerStylePostFit.Fit"/>. 05.2 plan 04 (D-04)
        /// / 05.3: the forced pass is composed with the window's ATTACHED merged
        /// Style Asset — references and stylization both come from that one asset
        /// (the R6 Fit Look retirement removed the only UI caller; this seam
        /// survives as the D-02 complement + the S17 validation render donor for
        /// plan 06). A null asset, no loadable source, or no stylization output
        /// (<see cref="HasStylizedOutput"/>, the A7 prereq predicate) returns the
        /// prereq-unmet bucket (notExtracted). Cancellable progress bar, extractor
        /// read discipline (isReadable pre-check, per-texture failure name), and no
        /// partial result on cancel or failure.
        /// </summary>
        internal NamerStylePostFitRunResult RunStylePostFit(NamerStyleAsset asset)
        {
            if (asset == null)
            {
                return new NamerStylePostFitRunResult(cancelled: false, errorTextureName: null, notExtracted: true);
            }

            // WR-03 (05.1 review): no _pipeline pre-check here. _pipeline is created
            // lazily by the debounced preview recompute, so in the window between
            // loading a source and the first recompute the old check returned
            // NotExtracted == true — a completely silent no-op (the button state has
            // no locked reason for it, and the profile IS extracted). The stage-2
            // forced RecomputePreview below runs EnsurePipeline() first, which
            // creates the pipeline on demand; a source that still produces no live
            // result fails loudly at the stylizedBase null check instead.
            if (!HasLoadableSource || !HasStylizedOutput(_settings))
            {
                return new NamerStylePostFitRunResult(cancelled: false, errorTextureName: null, notExtracted: true);
            }

            // Extract's first-line precedent (T-05-03): sanitize a hand-edited asset
            // before any weight or reference is read.
            asset.ClampToCaps();

            string exemplarName = _selection.name;
            try
            {
                // Stage 1 — read the references (extractor discipline: one at a time,
                // isReadable pre-check so the failure is a named-texture result, buffer
                // dropped before the next reference).
                NamerStylePostStats referenceStats = new NamerStylePostStats();
                for (int i = 0; i < asset.References.Count; i++)
                {
                    Texture2D reference = asset.References[i];
                    if (reference == null)
                    {
                        continue;
                    }

                    float weight = i < asset.ReferenceWeights.Count
                        ? asset.ReferenceWeights[i]
                        : NamerEditorConstants.DefaultReferenceWeight;

                    if (!reference.isReadable)
                    {
                        return new NamerStylePostFitRunResult(cancelled: false, errorTextureName: reference.name, notExtracted: false);
                    }

                    Color[] pixels;
                    try
                    {
                        pixels = reference.GetPixels();
                    }
                    catch (Exception)
                    {
                        return new NamerStylePostFitRunResult(cancelled: false, errorTextureName: reference.name, notExtracted: false);
                    }

                    NamerStylePostFit.AccumulateReference(referenceStats, pixels, weight);
                    pixels = null; // T-05-02 extractor precedent: drop the buffer before the next ref.

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Fitting NAMER Post Look",
                            "Reading references (" + reference.name + ")",
                            FitStageProgress(0, (float)(i + 1) / asset.References.Count)))
                    {
                        return new NamerStylePostFitRunResult(cancelled: true, errorTextureName: null, notExtracted: false);
                    }
                }

                // Stage 2 — forced-stylized recompute + sync readback. The flag is
                // ALWAYS reset in the finally; the side effect is honest (the preview
                // pane keeps showing the stylized result that was just fitted).
                // 05.2 plan 04 (D-04) / 05.3: the forced pass is composed with the
                // window's ATTACHED merged Style Asset — references and
                // stylization both come from that one asset. The WR-02 override is
                // retired: a window with no attached asset is now an A7 prereq
                // failure (HasStylizedOutput) resolved before the fit starts,
                // never a silently unstylized base mid-fit.
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Fitting NAMER Post Look",
                        "Rendering stylized preview",
                        FitStageProgress(1, 0f)))
                {
                    return new NamerStylePostFitRunResult(cancelled: true, errorTextureName: null, notExtracted: false);
                }

                _forceStylizeForFit = true;
                try
                {
                    RecomputePreview();
                }
                finally
                {
                    _forceStylizeForFit = false;
                }

                RenderTexture stylizedBase = (RenderTexture)ResolveStylizePreviewBase(_liveResult);
                if (stylizedBase == null)
                {
                    throw new InvalidOperationException(
                        "the stylized preview of '" + exemplarName + "' produced no result; no fit was written");
                }

                // RGBAFloat (not RGBA32): the buffer is viewed with GetData<Color>
                // (16 bytes/element). A 4-byte-per-pixel RGBA32 request pairs with
                // GetData<Color32> only — mixing them hands the fit w*h/4 sixteen-byte
                // garbage "pixels" (texel byte-quads reinterpreted as ~-1e38 floats)
                // that tonemap to 0 and rail every fitted knob (fit-look-rails fix).
                // RGBAFloat also matches AccumulateSource's HDR-linear input domain
                // (the AO un-multiply divide can exceed 1) and the decomp/roughness
                // float-RT readback convention.
                AsyncGPUReadbackRequest request =
                    NamerComputePipeline.RequestReadback(stylizedBase, 0, TextureFormat.RGBAFloat);
                request.forcePlayerLoopUpdate = true;
                request.WaitForCompletion();
                if (request.hasError)
                {
                    throw new InvalidOperationException(
                        "GPU readback of the stylized preview of '" + exemplarName + "' failed; no fit was written");
                }

                Color[] readbackColors = request.GetData<Color>().ToArray();

                // Stage 3 — deterministic fit.
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Fitting NAMER Post Look",
                        "Fitting parameters",
                        FitStageProgress(2, 0f)))
                {
                    return new NamerStylePostFitRunResult(cancelled: true, errorTextureName: null, notExtracted: false);
                }

                NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(readbackColors);
                NamerStylePostFitParams fitted =
                    NamerStylePostFit.Fit(sourceStats, referenceStats, asset.ComputeFingerprint());
                return new NamerStylePostFitRunResult(fitted, exemplarName);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        /// <summary>
        /// Overall Fit Look progress for one stage fraction (stage 0 = references,
        /// 1 = stylized preview, 2 = the fit itself).
        /// </summary>
        private static float FitStageProgress(int stageIndex, float fraction)
        {
            return (stageIndex + Mathf.Clamp01(fraction)) / kPostFitStageCount;
        }

        /// <summary>
        /// 05.2 plan 04 (A7 prereq, D-04) / 05.3: true when the fit would have a
        /// stylized output to decompose — a resolvable ATTACHED merged Style Asset
        /// exists (the forced pass composes with that one asset; references and
        /// stylization both come from it — STYL-15). Internal static seam (no UI
        /// change) so the window-state tests stay headless-testable.
        /// </summary>
        internal static bool HasStylizedOutput(NamerProcessorSettings settings)
        {
            return NamerProcessor.ResolveStyleAsset(settings) != null;
        }

        /// <summary>
        /// 05.3 W18 attach-state honesty line (UI-SPEC locked copy,
        /// NamerEditorConstants formats): "" when nothing is attached (the W18
        /// popup-disabled hint carries the null state); otherwise the decomposed
        /// line counts palette colors via <see cref="NamerStyleAsset.PaletteAnchors"/>
        /// and POSITIVE-WEIGHT references only (weight-0 slots are skipped
        /// everywhere — A4), or the not-decomposed line.
        /// </summary>
        internal static string DescribeStyleAssetState(NamerStyleAsset asset)
        {
            if (asset == null)
            {
                return string.Empty;
            }

            if (asset.IsDecomposed)
            {
                int weightedReferences = 0;
                for (int i = 0; i < asset.References.Count; i++)
                {
                    float weight = i < asset.ReferenceWeights.Count
                        ? asset.ReferenceWeights[i]
                        : NamerEditorConstants.DefaultReferenceWeight;
                    if (asset.References[i] != null && weight > 0f)
                    {
                        weightedReferences++;
                    }
                }

                return string.Format(
                    NamerEditorConstants.StyleAssetStateDecomposedFormat,
                    asset.name,
                    asset.PaletteAnchors.Count,
                    weightedReferences);
            }

            return string.Format(NamerEditorConstants.StyleAssetStateNotDecomposedFormat, asset.name);
        }

        /// <summary>
        /// 05-03 W4 lens composition (05.3 retarget): the stylization handed to
        /// <see cref="NamerComputePipeline.Process"/> for the PREVIEW — null unless
        /// a merged Style Asset is attached AND After View reads a stylized-side
        /// lens (the RunStylePostFit forced pass pins the view to
        /// kAfterViewStylized via <see cref="_forceStylizeForFit"/>). 05.1 W5:
        /// Stylized + Post composes the SAME stylization (the post grade rides the
        /// preview material's uniform table, not the pipeline — nothing
        /// post-processed touches the compute output). 05.2-03 (STYL-11): the
        /// palette never pulls luma, so there is no tone exclusivity to arbitrate —
        /// both stylized lenses compose the identical context. Internal for the
        /// lens logic test (the smallest honest seam).
        /// </summary>
        internal static NamerStyleStylization ComposePreviewStylization(
            NamerStyleAsset asset, int afterView, NamerSplitResult split)
        {
            if (asset == null || (afterView != kAfterViewStylized && afterView != kAfterViewPost))
            {
                return null;
            }

            return NamerStyleStylization.Create(asset, split);
        }

        /// <summary>
        /// The stylize albedo preview's base texture: the styled SOURCE base
        /// (<see cref="NamerComputeResult.SourceBaseColor"/> — pre-projection, Phase 5
        /// D-02: the styled source) when the 04.2 projection ran, else
        /// <see cref="NamerComputeResult.NormalizedBaseColor"/> (which IS the styled base
        /// when no projection ran). Internal static pure seam mirroring
        /// <see cref="ComposePreviewStylization"/> — headless-testable reference selection.
        /// </summary>
        internal static Texture ResolveStylizePreviewBase(NamerComputeResult result)
        {
            if (result == null)
            {
                return null;
            }

            return result.SourceBaseColor != null ? result.SourceBaseColor : result.NormalizedBaseColor;
        }

        /// <summary>
        /// The albedo preview's neutral-state reason line: null when both prerequisites
        /// hold (no line rendered), the no-source line when the Source pick is missing
        /// (e.g. a domain reload wiped a scene-object pick — the accepted Task 1
        /// limitation), else the no-result line (source picked, recompute pending).
        /// Internal static pure seam mirroring <see cref="ResolveStylizePreviewBase"/> —
        /// headless-testable.
        /// </summary>
        internal static string ResolveAlbedoPreviewEmptyReason(bool hasSource, bool hasLiveResult)
        {
            if (!hasSource)
            {
                return AlbedoPreviewNoSourceCaption;
            }

            return hasLiveResult ? null : AlbedoPreviewNoResultCaption;
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

            // Error Threshold + Coverage Target + Residual Resolution only act when residual
            // writing is on (04.2: the checkbox is the residual gate now; the threshold,
            // coverage, and resolution search keys on source-reconstruction error for the
            // written EXR). They are rendered disabled (EditorGUI.DisabledScope) with a
            // one-line hint when Write Residual is off — matching the phase's disable-state
            // conventions.
            using (new EditorGUI.DisabledScope(!_writeResidual))
            {
                float newThreshold = EditorGUILayout.Slider(
                    new GUIContent(
                        "Error Threshold",
                        "Maximum acceptable reconstruction error for the adaptive residual-resolution search (used when "
                            + "Write Residual is on). Recomputes the preview in memory "
                            + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."),
                    _errorThreshold, 0f, 0.25f);
                if (!Mathf.Approximately(newThreshold, _errorThreshold))
                {
                    _errorThreshold = newThreshold;
                    _settings.ErrorThreshold = newThreshold;
                    _afterPanelState.MarkTweaking();
                    MarkDirty();
                }

                float newCoverage = EditorGUILayout.Slider(
                    new GUIContent(
                        "Coverage Target",
                        "Fraction of UV-covered texels that must stay within the Error Threshold at a candidate residual "
                            + "resolution — the percentile gate of the adaptive search (D-05). Higher targets keep larger "
                            + "residuals; when no rung meets the target the full source long edge is kept. Recomputes the "
                            + "preview in memory " + NamerEditorConstants.DebounceSeconds
                            + " s after the slider stops — nothing is written to disk."),
                    _coverageTarget, 0.90f, 1.00f);
                newCoverage = Mathf.Round(newCoverage / kCoverageSliderStep) * kCoverageSliderStep;
                if (!Mathf.Approximately(newCoverage, _coverageTarget))
                {
                    _coverageTarget = newCoverage;
                    _settings.CoverageTarget = newCoverage;
                    _afterPanelState.MarkTweaking();
                    MarkDirty();
                }

                int newResolution = EditorGUILayout.Popup(
                    new GUIContent(
                        "Residual Resolution",
                        "Residual texture resolution. Auto adaptively halves from the source resolution while the Coverage Target fraction of covered texels stays within the Error Threshold; manual options snap to the same halving steps."),
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
        /// written (one-texture) and "written @ Npx, coverage X%" (the achieved-coverage
        /// percentile-gate statistic, D-07) when the EXR is written.
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
                    "Whether the residual EXR was written (the 04.2 Write Residual checkbox). The coverage is the "
                    + "percentile-gate statistic at the chosen resolution: the fraction of UV-covered texels within "
                    + "the Error Threshold. When no ladder rung meets the Coverage Target, the full source long edge "
                    + "is kept (D-08) — the row then shows the source long edge, honestly."),
                new GUIContent(residualWritten
                    ? "written @" + _decompStats.ChosenResolution + "px, coverage "
                        + (_decompStats.AchievedCoverage * 100f).ToString("0.#") + "%"
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
