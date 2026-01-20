using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Click-Click Road Builder with snapping:
/// - 1st LMB: set start (snaps to nearest road if within snapDistance)
/// - Move mouse: preview updates with snapping to end as well
/// - 2nd LMB: build road (segmented). Stores endpoint directions in Road component
/// - SHIFT/ALT: force straight line (ignore curves/right-angle rule)
/// - If snapping to the middle of a road, approach perpendicularly (right angle)
/// - RMB during preview: cancel road
/// - RMB idle: delete clicked chunk only
/// </summary>
[DefaultExecutionOrder(-10)]
[RequireComponent(typeof(PlayerInput))]
public class RoadBuilder : MonoBehaviour
{
    #region Inspector Settings
    [Header("Input")]
    [SerializeField] private PlayerInput playerInput;

    [Header("Raycast")]
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float rayMaxDistance = 2000f;

    [Header("Appearance")]
    [SerializeField] private float roadWidth = 2f;
    [SerializeField] private Material roadMaterial;
    [SerializeField] private float uvTilingPerMeter = 1f;

    [Header("Curve")]
    [SerializeField] private float samplesPerMeter = 1.5f;

    [Header("Alt Curve (Sin Wave)")]
    [SerializeField] private float altCurveStrength = 0.3f;
    [SerializeField, Min(0)] private float curveStrengthStep = 0.1f;
    [SerializeField, Min(0)] private float maxCurveStrength = 2f;

    [Header("Segmentation / Snapping")]
    [SerializeField] private float segmentLength = 6f;
    [SerializeField] private float snapDistance = 0.5f;
    [SerializeField] private Vector3 up = default;

    [Header("Preview")]
    [SerializeField] private Color previewColor = new Color(1f, 0.85f, 0.2f, 0.9f);

    [Header("GuideLine")]
    [SerializeField] private bool showGuideLine = true;
    [SerializeField] private Color guideLineColor = Color.cyan;
    [SerializeField] private float guideLineWidth = 0.1f;

    [Header("Direction Snap")]
    [SerializeField] private bool enableDirectionSnap = true;
    [SerializeField, Range(1f, 22.5f)] private float directionSnapAngle = 10f;
    #endregion

    #region Constants
    private const float SCROLL_THRESHOLD = 0.01f;
    #endregion

    #region Runtime State
    [SerializeField] private bool _buildModeEnabled = true;
    public bool BuildModeEnabled
    {
        get => _buildModeEnabled;
        set
        {
            if (_buildModeEnabled == value) return;
            _buildModeEnabled = value;
            if (!_buildModeEnabled && _isPreviewing)
            {
                _isPreviewing = false;
                if (_previewFirstGO) _previewFirstGO.SetActive(false);
                if (_previewMidGO) _previewMidGO.SetActive(false);
                if (_previewEndGO) _previewEndGO.SetActive(false);
                if (_guideLineGO) _guideLineGO.SetActive(false);
            }
        }
    }

    private bool _initialized;
    private Camera _cam;
    private Transform _roadsParent;
    private Transform _pooledRoadsParent;
    private Transform _pooledChunksParent;

    // Input State
    private bool _isStraightModifierPressed;
    private bool _isCurveModifierPressed;
    private float _scrollWheelValue;

    // Pooling
    private readonly Queue<GameObject> _roadPool = new();
    private readonly Queue<GameObject> _chunkPool = new();

    private bool _isPreviewing;
    private Vector3 _startAnchor;
    private GameObject _previewFirstGO;
    private GameObject _previewMidGO;
    private GameObject _previewEndGO;
    private MeshFilter _previewFirstMF;
    private MeshFilter _previewMidMF;
    private MeshFilter _previewEndMF;
    private MeshRenderer _previewFirstMR;
    private MeshRenderer _previewMidMR;
    private MeshRenderer _previewEndMR;

    // Alt key curve control
    private bool _wasAltPressed;
    private Vector3 _bezierReferencePoint;

    private bool _startSnapped;
    private bool _endSnapped;

    // GuideLine
    private GameObject _guideLineGO;
    private LineRenderer _guideLineRenderer;
    private Vector3 _guideLineDirection;

    // Direction Snap
    private List<Vector3> _baseDirections = new();
    private Vector3 _snappedDirection;
    private bool _isDirectionSnapped;

    private readonly List<RoadComponent> _roads = new();
    private readonly List<RoadChunkRef> _chunks = new();
    private readonly List<RoadCapRef> _caps = new();

    public RoadComponent LastRoad { get; private set; }
    #endregion

    #region Pooling
    private GameObject GetRoadFromPool()
    {
        if (_roadPool.Count > 0)
        {
            var pooledRoad = _roadPool.Dequeue();
            pooledRoad.SetActive(true);
            return pooledRoad;
        }

        var roadGO = new GameObject($"Road_{_roads.Count}");
        roadGO.transform.SetParent(_roadsParent, false);
        roadGO.layer = gameObject.layer;
        roadGO.AddComponent<RoadComponent>();
        return roadGO;
    }

    private GameObject GetChunkFromPool()
    {
        if (_chunkPool.Count > 0)
        {
            var pooledChunk = _chunkPool.Dequeue();
            pooledChunk.SetActive(true);
            pooledChunk.transform.SetParent(_roadsParent, false);
            return pooledChunk;
        }

        var chunkGO = new GameObject("PooledChunk");
        chunkGO.layer = gameObject.layer;
        chunkGO.transform.SetParent(_roadsParent, false);
        chunkGO.AddComponent<MeshFilter>();
        chunkGO.AddComponent<MeshRenderer>();
        chunkGO.AddComponent<MeshCollider>();
        return chunkGO;
    }

    private void ReturnChunkToPool(GameObject chunk)
    {
        if (chunk == null) return;

        chunk.SetActive(false);
        chunk.transform.SetParent(_pooledChunksParent, false);
        _chunkPool.Enqueue(chunk);

        _chunks.RemoveAll(c => c.go == chunk);
        _caps.RemoveAll(c => c.go == chunk);
    }
    #endregion

    #region Initialization
    private void Awake() => InitOnce();

    private void InitOnce()
    {
        if (_initialized) return;
        _initialized = true;

        // PlayerInput 초기화
        if (playerInput == null)
            playerInput = GetComponent<PlayerInput>();

        _cam = Camera.main;
        if (up == Vector3.zero) up = Vector3.up;

        if (!roadMaterial)
        {
            roadMaterial = new Material(Shader.Find("Standard"));
            roadMaterial.enableInstancing = true;
        }

        _roadsParent = new GameObject("Roads").transform;
        _roadsParent.SetParent(transform, false);

        _pooledRoadsParent = new GameObject("PooledRoads").transform;
        _pooledRoadsParent.SetParent(transform, false);

        _pooledChunksParent = new GameObject("PooledChunks").transform;
        _pooledChunksParent.SetParent(transform, false);

        CreatePreviewMesh();
        CreateGuideLine();
    }

    private void CreatePreviewMesh()
    {
        _previewFirstGO = new GameObject("RoadPreview_First");
        _previewFirstGO.transform.SetParent(transform, false);
        _previewFirstMF = _previewFirstGO.AddComponent<MeshFilter>();
        _previewFirstMR = _previewFirstGO.AddComponent<MeshRenderer>();
        _previewFirstMR.sharedMaterial = roadMaterial;
        _previewFirstMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewFirstMR.receiveShadows = false;
        SetMaterialColor(_previewFirstMR, Color.green);
        _previewFirstGO.SetActive(false);

        _previewMidGO = new GameObject("RoadPreview_Mid");
        _previewMidGO.transform.SetParent(transform, false);
        _previewMidMF = _previewMidGO.AddComponent<MeshFilter>();
        _previewMidMR = _previewMidGO.AddComponent<MeshRenderer>();
        _previewMidMR.sharedMaterial = roadMaterial;
        _previewMidMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewMidMR.receiveShadows = false;
        SetMaterialColor(_previewMidMR, previewColor);
        _previewMidGO.SetActive(false);

        _previewEndGO = new GameObject("RoadPreview_End");
        _previewEndGO.transform.SetParent(transform, false);
        _previewEndMF = _previewEndGO.AddComponent<MeshFilter>();
        _previewEndMR = _previewEndGO.AddComponent<MeshRenderer>();
        _previewEndMR.sharedMaterial = roadMaterial;
        _previewEndMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewEndMR.receiveShadows = false;
        SetMaterialColor(_previewEndMR, Color.red);
        _previewEndGO.SetActive(false);
    }

    private void CreateGuideLine()
    {
        _guideLineGO = new GameObject("GuideLine");
        _guideLineGO.transform.SetParent(transform, false);

        _guideLineRenderer = _guideLineGO.AddComponent<LineRenderer>();
        _guideLineRenderer.startWidth = guideLineWidth;
        _guideLineRenderer.endWidth = guideLineWidth;
        _guideLineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        _guideLineRenderer.startColor = guideLineColor;
        _guideLineRenderer.endColor = guideLineColor;
        _guideLineRenderer.positionCount = 2;
        _guideLineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _guideLineRenderer.receiveShadows = false;
        _guideLineRenderer.useWorldSpace = true;

        _guideLineGO.SetActive(false);
    }
    #endregion

    #region Input & Update
    private void Update()
    {
        if (!_cam) _cam = Camera.main;

        HandleAltCurveAdjustment();
        HandlePreview();
    }

    // ========================================
    // New Input System Public Methods
    // Connect to PlayerInput Events
    // ========================================

    /// <summary>
    /// [PlayerInput Events] LeftClick action
    /// </summary>
    public void OnLeftClick(InputAction.CallbackContext context)
    {
        if (!BuildModeEnabled) return;
        if (context.performed) DoClick();
    }

    /// <summary>
    /// [PlayerInput Events] RightClick action
    /// </summary>
    public void OnRightClick(InputAction.CallbackContext context)
    {
        if (!BuildModeEnabled) return;
        if (context.performed) DoRightClick();
    }

    /// <summary>
    /// [PlayerInput Events] Cancel action (ESC)
    /// </summary>
    public void OnCancel(InputAction.CallbackContext context)
    {
        if (context.performed) DoCancel();
    }

    /// <summary>
    /// [PlayerInput Events] StraightModifier action (Shift)
    /// </summary>
    public void OnStraightModifier(InputAction.CallbackContext context)
    {
        if (context.started) _isStraightModifierPressed = true;
        else if (context.canceled) _isStraightModifierPressed = false;
    }

    /// <summary>
    /// [PlayerInput Events] PressedCurveModifier action (Alt pressed)
    /// </summary>
    public void OnPressedCurveModifier(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            _isCurveModifierPressed = true;

            if (!_wasAltPressed && _isPreviewing)
            {
                if (RayToGround(out var currentMousePos))
                {
                    _bezierReferencePoint = currentMousePos;
                }
            }
            _wasAltPressed = true;
        }
    }

    /// <summary>
    /// [PlayerInput Events] ReleasedCurveModifier action (Alt released)
    /// </summary>
    public void OnReleasedCurveModifier(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            _isCurveModifierPressed = false;
            _wasAltPressed = false;
        }
    }

    /// <summary>
    /// [PlayerInput Events] ScrollWheel action
    /// </summary>
    public void OnScrollWheel(InputAction.CallbackContext context)
    {
        if (context.performed) _scrollWheelValue = context.ReadValue<float>();
        else if (context.canceled) _scrollWheelValue = 0f;
    }

    // ========================================
    // Input Processing
    // ========================================

    private void DoClick()
    {
        if (!_isPreviewing) StartPreview();
        else BuildRoad();
    }

    private void DoRightClick()
    {
        if (_isPreviewing) StopPreview();
        else TryDeleteChunkUnderMouse();
    }

    private void DoCancel()
    {
        if (_isPreviewing) StopPreview();
    }

    private void StartPreview()
    {
        if (!RayToGround(out var hitPos)) return;
        SetStartAnchor(hitPos);
        _isPreviewing = true;
        _bezierReferencePoint = Vector3.zero;
        _wasAltPressed = false;
        _previewFirstGO.SetActive(true);
        _previewMidGO.SetActive(true);
        _previewEndGO.SetActive(true);

        if (showGuideLine && _guideLineGO)
        {
            _guideLineGO.SetActive(true);
            if (RayToGround(out var currentMousePos))
            {
                _guideLineDirection = (currentMousePos - _startAnchor).normalized;
            }
        }
    }

    private void BuildRoad()
    {
        if (!RayToGround(out var endPos)) return;

        Vector3 adjustedEndPos = endPos;
        if (_isDirectionSnapped && _snappedDirection != Vector3.zero && !_isCurveModifierPressed)
        {
            float distance = Vector3.Distance(_startAnchor, endPos);
            adjustedEndPos = _startAnchor + _snappedDirection * distance;
        }

        var centerline = CreateCenterline(_startAnchor, adjustedEndPos, out bool endSnapped);
        if (centerline == null || centerline.Count < 2) return;

        _endSnapped = endSnapped;
        LastRoad = CreateNewRoad(centerline, _startSnapped, _endSnapped);
        LastRoad.UpdateCaps();

        SetStartAnchor(adjustedEndPos);
    }

    private void HandlePreview()
    {
        if (!_isPreviewing || !RayToGround(out var mousePos)) return;

        UpdateGuideLine(mousePos);

        Vector3 adjustedEndPos = mousePos;
        if (_isDirectionSnapped && _snappedDirection != Vector3.zero && !_isCurveModifierPressed)
        {
            float distance = Vector3.Distance(_startAnchor, mousePos);
            adjustedEndPos = _startAnchor + _snappedDirection * distance;
        }

        var centerline = CreateCenterline(_startAnchor, adjustedEndPos, out _);
        UpdatePreviewMesh(centerline);
    }

    private void SetStartAnchor(Vector3 position)
    {
        _startSnapped = TryFindSnap(position, out var sPoint, out var sTan, out var sIsEnd, out var baseDirections);
        _startAnchor = _startSnapped ? sPoint : position;

        _baseDirections.Clear();
        if (_startSnapped && baseDirections != null)
        {
            _baseDirections.AddRange(baseDirections);
        }
        _isDirectionSnapped = false;
        _snappedDirection = Vector3.zero;
    }

    private List<Vector3> CreateCenterline(Vector3 start, Vector3 end, out bool endSnapped)
    {
        var centerline = BuildCenterlineWithSnap(start, end, _isStraightModifierPressed, _isCurveModifierPressed, out _);
        endSnapped = TryFindSnap(end, out _, out _, out _);
        return centerline;
    }

    private void StopPreview()
    {
        _isPreviewing = false;
        _bezierReferencePoint = Vector3.zero;
        _wasAltPressed = false;
        if (_previewFirstGO) _previewFirstGO.SetActive(false);
        if (_previewMidGO) _previewMidGO.SetActive(false);
        if (_previewEndGO) _previewEndGO.SetActive(false);
        if (_guideLineGO) _guideLineGO.SetActive(false);

        _baseDirections.Clear();
        _isDirectionSnapped = false;
        _snappedDirection = Vector3.zero;
    }

    private void HandleAltCurveAdjustment()
    {
        if (!_isPreviewing) return;

        if (Mathf.Abs(_scrollWheelValue) > SCROLL_THRESHOLD)
        {
            float change = _scrollWheelValue * curveStrengthStep;
            altCurveStrength = Mathf.Clamp(altCurveStrength + change, 0f, maxCurveStrength);
            _scrollWheelValue = 0f;
        }
    }

    private bool RayToGround(out Vector3 pos)
    {
        if (!_cam)
        {
            pos = default;
            Debug.LogError("[RayToGround] Null exception: Camera for raycasting.");
            return false;
        }

        Vector3 mousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector3.zero;
        var ray = _cam.ScreenPointToRay(mousePosition);

        if (Physics.Raycast(ray, out var hit, rayMaxDistance, groundMask, QueryTriggerInteraction.Ignore))
        {
            pos = hit.point;
            return true;
        }
        pos = default;
        return false;
    }
    #endregion

    #region Bezier Curve Generation
    private List<Vector3> BuildCenterlineWithSnap(Vector3 start, Vector3 mouseEnd, bool straightMode, bool altCurveMode, out Vector3 endTangentUsed)
    {
        endTangentUsed = Vector3.zero;

        bool endSnapped = TryFindSnap(mouseEnd, out var ePoint, out var eTan, out var eIsEnd);
        var end = endSnapped ? ePoint : mouseEnd;

        if (straightMode || !altCurveMode)
            return BuildStraight(start, end);

        if (altCurveMode && _bezierReferencePoint != Vector3.zero)
        {
            return BuildCircularArc(start, end, _bezierReferencePoint);
        }

        return BuildStraight(start, end);
    }

    private List<Vector3> BuildStraight(Vector3 a, Vector3 b)
    {
        var line = new List<Vector3>(32);
        float dist = Vector3.Distance(a, b);
        int steps = Mathf.Max(2, Mathf.CeilToInt(dist * Mathf.Max(0.25f, samplesPerMeter)));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            line.Add(Vector3.Lerp(a, b, t));
        }
        return line;
    }

    private static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float u = 1f - t;
        return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
    }
    #endregion

    #region Alt Curve Generation (Bending Effect)
    private List<Vector3> BuildCircularArc(Vector3 start, Vector3 end, Vector3 referencePoint)
    {
        float distance = Vector3.Distance(start, end);
        if (distance < 0.001f) return BuildStraight(start, end);

        Vector3 referenceToEnd = end - referencePoint;
        if (referenceToEnd.sqrMagnitude < 0.01f)
        {
            return BuildStraight(start, referencePoint);
        }

        return BuildElasticCurve(start, end, referencePoint);
    }

    private List<Vector3> BuildElasticCurve(Vector3 start, Vector3 currentEnd, Vector3 referencePoint)
    {
        var line = new List<Vector3>(64);

        Vector3 startToRef = (referencePoint - start).normalized;
        float startToRefDist = Vector3.Distance(start, referencePoint);

        Vector3 refToCursor = (currentEnd - referencePoint).normalized;
        float refToCursorDist = Vector3.Distance(referencePoint, currentEnd);

        float totalDist = Vector3.Distance(start, currentEnd);

        float controlStrength1 = startToRefDist * 0.4f * altCurveStrength;
        Vector3 control1 = start + startToRef * controlStrength1;

        float startToCursorDist = Vector3.Distance(start, currentEnd);
        float lengthRatio = startToCursorDist / startToRefDist;

        Vector3 endTangentDirection;

        if (Mathf.Abs(lengthRatio - 1.0f) < 0.1f)
        {
            endTangentDirection = startToRef;
        }
        else
        {
            Vector3 refToCursorNorm = refToCursor.normalized;
            float refCursorRatio = refToCursorDist / startToRefDist;

            if (Mathf.Abs(refCursorRatio - 1.0f) < 0.1f)
            {
                Vector3 perpendicular = Vector3.Cross(up, startToRef).normalized;
                endTangentDirection = Vector3.Dot(refToCursorNorm, perpendicular) > 0 ? perpendicular : -perpendicular;
            }
            else
            {
                Vector3 baseDirection = startToRef;
                Vector3 perpendicular = Vector3.Cross(up, startToRef).normalized;

                float bendFactor = (lengthRatio - 1.0f) * 0.5f * altCurveStrength;

                Vector3 startToCursor = (currentEnd - start).normalized;
                float crossProduct = Vector3.Dot(Vector3.Cross(startToRef, startToCursor), up);
                bool isRightSide = crossProduct > 0;

                Vector3 bendDirection = isRightSide ? perpendicular : -perpendicular;
                endTangentDirection = (baseDirection + bendDirection * bendFactor).normalized;
            }
        }

        float controlStrength2 = refToCursorDist * 0.4f * altCurveStrength;
        Vector3 control2 = currentEnd - endTangentDirection * controlStrength2;

        Vector3 midPointBezier = 0.125f * start + 0.375f * control1 + 0.375f * control2 + 0.125f * currentEnd;
        Vector3 offset = referencePoint - midPointBezier;

        control2 += offset;

        int steps = Mathf.Max(8, Mathf.CeilToInt(totalDist * samplesPerMeter));

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 point = Bezier(start, control1, control2, currentEnd, t);
            line.Add(point);
        }

        return line;
    }
    #endregion

    #region Preview Management
    private void UpdatePreviewMesh(List<Vector3> centerline)
    {
        if (centerline == null || centerline.Count < 2)
        {
            _previewFirstGO.SetActive(false);
            _previewMidGO.SetActive(false);
            _previewEndGO.SetActive(false);
            return;
        }

        var segments = DivideCenterlineIntoThreeSegments(centerline);

        if (segments.first != null && segments.first.Count >= 2)
        {
            _previewFirstGO.SetActive(true);
            var firstMesh = MeshFromCenterline(segments.first, roadWidth, uvTilingPerMeter, up);
            _previewFirstMF.sharedMesh = firstMesh;
        }
        else
        {
            _previewFirstGO.SetActive(false);
        }

        if (segments.mid != null && segments.mid.Count >= 2)
        {
            _previewMidGO.SetActive(true);
            var midMesh = MeshFromCenterline(segments.mid, roadWidth, uvTilingPerMeter, up);
            _previewMidMF.sharedMesh = midMesh;
        }
        else
        {
            _previewMidGO.SetActive(false);
        }

        if (segments.end != null && segments.end.Count >= 2)
        {
            _previewEndGO.SetActive(true);
            var endMesh = MeshFromCenterline(segments.end, roadWidth, uvTilingPerMeter, up);
            _previewEndMF.sharedMesh = endMesh;
        }
        else
        {
            _previewEndGO.SetActive(false);
        }
    }

    private (List<Vector3> first, List<Vector3> mid, List<Vector3> end) DivideCenterlineIntoThreeSegments(List<Vector3> centerline)
    {
        if (centerline == null || centerline.Count < 2)
            return (null, null, null);

        float capLength = roadWidth * 0.5f;

        var mid = new List<Vector3>(centerline);
        var first = CreateExtensionSegment(centerline, true, capLength);
        var end = CreateExtensionSegment(centerline, false, capLength);

        return (first, mid, end);
    }

    private List<Vector3> CreateExtensionSegment(List<Vector3> centerline, bool isStart, float length)
    {
        if (centerline == null || centerline.Count < 2)
            return null;

        Vector3 direction;
        Vector3 startPoint;

        if (isStart)
        {
            direction = (centerline[0] - centerline[1]).normalized;
            startPoint = centerline[0];
        }
        else
        {
            direction = (centerline[centerline.Count - 1] - centerline[centerline.Count - 2]).normalized;
            startPoint = centerline[centerline.Count - 1];
        }

        var extension = new List<Vector3>();

        if (isStart)
        {
            Vector3 extensionPoint = startPoint + direction * length;
            extension.Add(extensionPoint);
            extension.Add(startPoint);
        }
        else
        {
            extension.Add(startPoint);
            Vector3 extensionPoint = startPoint + direction * length;
            extension.Add(extensionPoint);
        }

        return extension;
    }

    private void UpdateGuideLine(Vector3 mousePos)
    {
        if (!showGuideLine || !_guideLineGO || !_guideLineRenderer) return;

        if (!_isCurveModifierPressed)
        {
            Vector3 toMouse = mousePos - _startAnchor;
            if (toMouse.sqrMagnitude > 0.01f)
            {
                Vector3 mouseDir = toMouse.normalized;

                if (_startSnapped && enableDirectionSnap && _baseDirections.Count > 0)
                {
                    if (TrySnapDirection(mouseDir, out var snappedDir))
                    {
                        _guideLineDirection = snappedDir;
                        _snappedDirection = snappedDir;
                        _isDirectionSnapped = true;
                    }
                    else
                    {
                        _guideLineDirection = mouseDir;
                        _isDirectionSnapped = false;
                    }
                }
                else
                {
                    _guideLineDirection = mouseDir;
                    _isDirectionSnapped = false;
                }
            }
        }
        else
        {
            _isDirectionSnapped = false;
        }

        float distance = Vector3.Distance(_startAnchor, mousePos);
        Vector3 endPoint = _startAnchor + _guideLineDirection * distance;

        _guideLineRenderer.SetPosition(0, _startAnchor);
        _guideLineRenderer.SetPosition(1, endPoint);
    }
    #endregion

    #region Road Generation & Management
    private RoadComponent CreateNewRoad(List<Vector3> centerline, bool startConnected, bool endConnected)
    {
        var roadGO = GetRoadFromPool();
        var road = roadGO.GetComponent<RoadComponent>();

        var dirStart = DirectionAtStart(centerline);
        var dirEnd = DirectionAtEnd(centerline);

        road.Initialize(centerline, dirStart, dirEnd, roadWidth, startConnected, endConnected, RoadState.Active);

        CreateChunksUnderRoad(road, centerline, roadGO.transform, false);
        CreateCapsForRoad(road, roadGO.transform);

        _roads.Add(road);
        road.UpdateCaps();

        return road;
    }

    private void CreateChunksUnderRoad(RoadComponent road, List<Vector3> centerline, Transform parent, bool isPreview = false)
    {
        if (centerline == null || centerline.Count < 2) return;

        float acc = 0f;
        var pts = new List<Vector3> { centerline[0] };

        for (int i = 1; i < centerline.Count; i++)
        {
            float step = Vector3.Distance(centerline[i - 1], centerline[i]);
            if (acc + step < segmentLength)
            {
                pts.Add(centerline[i]);
                acc += step;
                continue;
            }

            float remain = segmentLength - acc;
            Vector3 dir = (centerline[i] - centerline[i - 1]);
            float len = dir.magnitude;

            if (len > 1e-4f)
            {
                Vector3 cut = centerline[i - 1] + dir.normalized * remain;
                pts.Add(cut);
                MakeChunk(road, pts, parent, isPreview);

                pts.Clear();
                pts.Add(cut);

                float leftover = step - remain;
                acc = 0f;

                while (leftover >= segmentLength)
                {
                    Vector3 cut2 = pts[pts.Count - 1] + dir.normalized * segmentLength;
                    pts.Add(cut2);
                    MakeChunk(road, pts, parent, isPreview);
                    pts.Clear();
                    pts.Add(cut2);
                    leftover -= segmentLength;
                }

                if (leftover > 0f)
                {
                    Vector3 tail = pts[pts.Count - 1] + dir.normalized * leftover;
                    pts.Add(tail);
                    acc = leftover;
                }
            }
        }

        if (pts.Count >= 2)
            MakeChunk(road, pts, parent, isPreview);
    }

    private void MakeChunk(RoadComponent road, List<Vector3> centerline, Transform parent, bool isPreview = false)
    {
        var go = GetChunkFromPool();
        go.name = $"RoadChunk_{road.ChunkCounter++}";
        go.transform.SetParent(parent, false);

        var mf = go.GetComponent<MeshFilter>();
        var mr = go.GetComponent<MeshRenderer>();
        var mc = go.GetComponent<MeshCollider>();

        mr.sharedMaterial = roadMaterial;

        if (isPreview)
        {
            SetMaterialColor(mr, previewColor);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        else
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;
        }

        var mesh = MeshFromCenterline(centerline, roadWidth, uvTilingPerMeter, up);
        mf.sharedMesh = mesh;
        mc.sharedMesh = mesh;
        mc.convex = false;

        _chunks.Add(new RoadChunkRef { go = go, mesh = mesh });
    }

    private void TryDeleteChunkUnderMouse()
    {
        if (!_cam) return;

        Vector3 mousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector3.zero;
        var ray = _cam.ScreenPointToRay(mousePosition);
        if (!Physics.Raycast(ray, out var hit, rayMaxDistance, ~0, QueryTriggerInteraction.Collide)) return;

        var go = hit.collider ? hit.collider.gameObject : null;
        if (!go) return;

        for (int i = 0; i < _chunks.Count; i++)
        {
            if (_chunks[i].go == go)
            {
                _chunks.RemoveAt(i);
                var road = go.GetComponentInParent<RoadComponent>();
                if (road) road.UpdateCaps();
                Destroy(go);
                return;
            }
        }

        for (int i = 0; i < _caps.Count; i++)
        {
            if (_caps[i].go == go)
            {
                _caps.RemoveAt(i);
                Destroy(go);
                return;
            }
        }
    }
    #endregion

    #region Snapping
    private bool TryFindSnap(Vector3 query, out Vector3 snapPoint, out Vector3 snapTangent, out bool isEndpoint, RoadComponent excludeRoad = null)
    {
        return TryFindSnap(query, out snapPoint, out snapTangent, out isEndpoint, out _, excludeRoad);
    }

    private bool TryFindSnap(Vector3 query, out Vector3 snapPoint, out Vector3 snapTangent, out bool isEndpoint, out List<Vector3> baseDirections, RoadComponent excludeRoad = null)
    {
        snapPoint = default;
        snapTangent = default;
        isEndpoint = false;
        baseDirections = new List<Vector3>();

        float bestDist = float.MaxValue;
        int bestIndex = -1;
        List<Vector3> bestCenterline = null;

        int layerMask = 1 << gameObject.layer;
        Collider[] colliders = Physics.OverlapSphere(query, snapDistance, layerMask);

        var checkedRoads = new HashSet<RoadComponent>();

        foreach (var collider in colliders)
        {
            if (collider == null) continue;

            var road = collider.GetComponentInParent<RoadComponent>();
            if (road == null || road.Centerline == null || road.Centerline.Count < 2) continue;

            if (excludeRoad != null && road == excludeRoad) continue;
            if (collider.name.Contains("Cap")) continue;
            if (checkedRoads.Contains(road)) continue;
            checkedRoads.Add(road);

            if (ClosestPointOnPolyline(road.Centerline, query, out var p, out float d, out int idx))
            {
                if (d < bestDist)
                {
                    bestDist = d;
                    snapPoint = p;
                    bestIndex = idx;
                    bestCenterline = road.Centerline;
                    snapTangent = Vector3.forward;
                }
            }
        }

        if (bestDist <= snapDistance && bestCenterline != null && bestIndex >= 0)
        {
            int n = bestCenterline.Count;

            if (bestIndex == 0)
            {
                isEndpoint = true;
                Vector3 dir = (bestCenterline[1] - bestCenterline[0]).normalized;
                baseDirections.Add(dir);
            }
            else if (bestIndex == n - 1)
            {
                isEndpoint = true;
                Vector3 dir = (bestCenterline[n - 1] - bestCenterline[n - 2]).normalized;
                baseDirections.Add(dir);
            }
            else
            {
                isEndpoint = false;
                Vector3 dirForward = (bestCenterline[bestIndex + 1] - bestCenterline[bestIndex]).normalized;
                Vector3 dirBackward = (bestCenterline[bestIndex - 1] - bestCenterline[bestIndex]).normalized;
                baseDirections.Add(dirForward);
                baseDirections.Add(dirBackward);
            }
        }

        return bestDist <= snapDistance;
    }

    private static bool ClosestPointOnPolyline(List<Vector3> centerlineVertices, Vector3 query, out Vector3 closestPoint, out float distance, out int closestIndex)
    {
        closestPoint = default;
        distance = float.MaxValue;
        closestIndex = -1;

        if (centerlineVertices == null || centerlineVertices.Count < 1) return false;

        for (int i = 0; i < centerlineVertices.Count; i++)
        {
            float d = Vector3.Distance(query, centerlineVertices[i]);
            if (d < distance)
            {
                distance = d;
                closestPoint = centerlineVertices[i];
                closestIndex = i;
            }
        }

        return distance < float.MaxValue;
    }

    private bool TrySnapDirection(Vector3 mouseDir, out Vector3 snappedDir)
    {
        snappedDir = mouseDir;

        if (!enableDirectionSnap || _baseDirections == null || _baseDirections.Count == 0)
            return false;

        mouseDir.y = 0;
        if (mouseDir.sqrMagnitude < 0.001f)
            return false;
        mouseDir.Normalize();

        float bestAngleDiff = float.MaxValue;
        Vector3 bestCandidate = mouseDir;

        float[] angleMultiples = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };

        foreach (var baseDir in _baseDirections)
        {
            Vector3 baseDirXZ = baseDir;
            baseDirXZ.y = 0;
            if (baseDirXZ.sqrMagnitude < 0.001f)
                continue;
            baseDirXZ.Normalize();

            foreach (float angleMult in angleMultiples)
            {
                Vector3 candidate = Quaternion.AngleAxis(angleMult, up) * baseDirXZ;
                float angleDiff = Vector3.Angle(mouseDir, candidate);

                if (angleDiff < bestAngleDiff)
                {
                    bestAngleDiff = angleDiff;
                    bestCandidate = candidate;
                }
            }
        }

        if (bestAngleDiff <= directionSnapAngle)
        {
            snappedDir = bestCandidate;
            return true;
        }

        return false;
    }
    #endregion

    #region Mesh Generation
    private static Mesh MeshFromCenterline(List<Vector3> cl, float width, float uvPerM, Vector3 up)
    {
        int n = cl.Count;
        var verts = new Vector3[n * 2];
        var uvs = new Vector2[n * 2];
        var tris = new int[(n - 1) * 6];

        float half = width * 0.5f;
        float running = 0f;

        for (int i = 0; i < n; i++)
        {
            Vector3 fwd;
            if (i == 0) fwd = (cl[1] - cl[0]).normalized;
            else if (i == n - 1) fwd = (cl[n - 1] - cl[n - 2]).normalized;
            else fwd = (cl[i + 1] - cl[i - 1]).normalized;

            Vector3 left = Vector3.Cross(up, fwd).normalized;

            var pL = cl[i] - left * half;
            var pR = cl[i] + left * half;

            verts[i * 2 + 0] = pL;
            verts[i * 2 + 1] = pR;

            if (i > 0) running += Vector3.Distance(cl[i - 1], cl[i]);
            float v = running * uvPerM;

            uvs[i * 2 + 0] = new Vector2(0, v);
            uvs[i * 2 + 1] = new Vector2(1, v);
        }

        int ti = 0;
        for (int i = 0; i < n - 1; i++)
        {
            int i0 = i * 2;
            int i1 = i * 2 + 1;
            int i2 = i * 2 + 2;
            int i3 = i * 2 + 3;

            tris[ti++] = i0; tris[ti++] = i2; tris[ti++] = i3;
            tris[ti++] = i0; tris[ti++] = i3; tris[ti++] = i1;
        }

        var mesh = new Mesh { name = "RoadRibbon" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Mesh MeshFromCap(Vector3 startLeft, Vector3 startRight, Vector3 endLeft, Vector3 endRight, Vector3 up)
    {
        var verts = new Vector3[4];
        var uvs = new Vector2[4];
        var tris = new int[6];

        verts[0] = startLeft;
        verts[1] = startRight;
        verts[2] = endLeft;
        verts[3] = endRight;

        uvs[0] = new Vector2(0, 0);
        uvs[1] = new Vector2(1, 0);
        uvs[2] = new Vector2(0, 1);
        uvs[3] = new Vector2(1, 1);

        tris[0] = 0; tris[1] = 2; tris[2] = 3;
        tris[3] = 0; tris[4] = 3; tris[5] = 1;

        var mesh = new Mesh { name = "RoadCap" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
    #endregion

    #region Cap
    private void CreateCapsForRoad(RoadComponent road, Transform parent)
    {
        if (road.LeftEdgeLine == null || road.RightEdgeLine == null) return;
        if (road.LeftEdgeLine.Count > 1 && road.RightEdgeLine.Count > 1)
        {
            road.ChunksCap_First = CreateCapForRoad(
                road.LeftEdgeLine[0], road.RightEdgeLine[0],
                road.LeftEdgeLine[1], road.RightEdgeLine[1],
                road, "FrontCap", parent
            );

            int lastIdx = road.LeftEdgeLine.Count - 1;
            int secondLastIdx = road.LeftEdgeLine.Count - 2;
            road.ChunksCap_End = CreateCapForRoad(
                road.LeftEdgeLine[secondLastIdx], road.RightEdgeLine[secondLastIdx],
                road.LeftEdgeLine[lastIdx], road.RightEdgeLine[lastIdx],
                road, "EndCap", parent
            );
        }
    }

    private GameObject CreateCapForRoad(Vector3 startLeft, Vector3 startRight, Vector3 endLeft, Vector3 endRight, RoadComponent roadComponent, string capName, Transform parent)
    {
        var capGO = GetChunkFromPool();
        capGO.name = capName;
        capGO.transform.SetParent(parent, false);

        var mf = capGO.GetComponent<MeshFilter>();
        var mr = capGO.GetComponent<MeshRenderer>();
        var mc = capGO.GetComponent<MeshCollider>();

        mr.sharedMaterial = roadMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        mr.receiveShadows = true;

        var mesh = MeshFromCap(startLeft, startRight, endLeft, endRight, up);
        mf.sharedMesh = mesh;
        mc.sharedMesh = mesh;
        mc.convex = false;

        _caps.Add(new RoadCapRef { go = capGO, mesh = mesh });
        return capGO;
    }
    #endregion

    #region Utilities
    private static void SetMaterialColor(Renderer r, Color c)
    {
        if (r && r.material && r.material.HasProperty("_Color"))
            r.material.color = c;
    }

    private static Vector3 DirectionAtStart(List<Vector3> cl)
    {
        return (cl.Count >= 2 && (cl[1] - cl[0]).sqrMagnitude > 1e-6f)
            ? (cl[1] - cl[0]).normalized : Vector3.forward;
    }

    private static Vector3 DirectionAtEnd(List<Vector3> cl)
    {
        int n = cl.Count;
        return (n >= 2 && (cl[n - 1] - cl[n - 2]).sqrMagnitude > 1e-6f)
            ? (cl[n - 1] - cl[n - 2]).normalized : DirectionAtStart(cl);
    }
    #endregion

    #region Data
    private struct RoadChunkRef
    {
        public GameObject go;
        public Mesh mesh;
    }

    private struct RoadCapRef
    {
        public GameObject go;
        public Mesh mesh;
    }
    #endregion
}
