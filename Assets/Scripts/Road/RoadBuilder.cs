using System.Collections.Generic;
using UnityEngine;


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
public class RoadBuilder : MonoBehaviour
{
    #region Inspector Settings
    [Header("Raycast / Input")]
    [SerializeField] private LayerMask groundMask;
    [SerializeField] private float rayMaxDistance = 2000f;

    [Header("Appearance")]
    [SerializeField] private float roadWidth = 2f;
    [SerializeField] private Material roadMaterial;
    [SerializeField] private float uvTilingPerMeter = 1f;

    [Header("Curve")]
    [SerializeField] private float samplesPerMeter = 1.5f;
    [SerializeField] private float handleLenRatio = 0.25f;
    [SerializeField] private float maxHandleLen = 8f;

    [Header("Alt Curve (Sin Wave)")]
    [SerializeField] private float altCurveStrength = 0.3f;

    [Header("Segmentation / Snapping")]
    [SerializeField] private float segmentLength = 6f;
    [SerializeField] private float snapDistance = 0.5f;
    [SerializeField] private Vector3 up = default;

    [Header("Preview")]
    [SerializeField] private Color previewColor = new Color(1f, 0.85f, 0.2f, 0.9f);
    #endregion

    #region Constants
    private const float WHEEL_SENSITIVITY = 0.1f;
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
            }
        }
    }

    private bool _initialized;
    private Camera _cam;
    private Transform _roadsParent;
    private Transform _pooledRoadsParent;
    private Transform _pooledChunksParent;

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

        // 새로 생성
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
            // 풀에서 나올 때는 임시로 _roadsParent에 두고, 실제 사용 시 적절한 부모로 설정됨
            pooledChunk.transform.SetParent(_roadsParent, false);
            return pooledChunk;
        }

        // 새로 생성
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

        // _chunks와 _caps 리스트에서 제거
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

        // 프리뷰용 단일 메시 생성
        CreatePreviewMesh();
    }

    private void CreatePreviewMesh()
    {
        // First segment (시작 부분)
        _previewFirstGO = new GameObject("RoadPreview_First");
        _previewFirstGO.transform.SetParent(transform, false);
        _previewFirstMF = _previewFirstGO.AddComponent<MeshFilter>();
        _previewFirstMR = _previewFirstGO.AddComponent<MeshRenderer>();
        _previewFirstMR.sharedMaterial = roadMaterial;
        _previewFirstMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewFirstMR.receiveShadows = false;
        SetMaterialColor(_previewFirstMR, Color.green); // 시작 부분은 녹색
        _previewFirstGO.SetActive(false);

        // Mid segment (중간 부분)
        _previewMidGO = new GameObject("RoadPreview_Mid");
        _previewMidGO.transform.SetParent(transform, false);
        _previewMidMF = _previewMidGO.AddComponent<MeshFilter>();
        _previewMidMR = _previewMidGO.AddComponent<MeshRenderer>();
        _previewMidMR.sharedMaterial = roadMaterial;
        _previewMidMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewMidMR.receiveShadows = false;
        SetMaterialColor(_previewMidMR, previewColor); // 중간 부분은 기본 프리뷰 색상
        _previewMidGO.SetActive(false);

        // End segment (끝 부분)
        _previewEndGO = new GameObject("RoadPreview_End");
        _previewEndGO.transform.SetParent(transform, false);
        _previewEndMF = _previewEndGO.AddComponent<MeshFilter>();
        _previewEndMR = _previewEndGO.AddComponent<MeshRenderer>();
        _previewEndMR.sharedMaterial = roadMaterial;
        _previewEndMR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _previewEndMR.receiveShadows = false;
        SetMaterialColor(_previewEndMR, Color.red); // 끝 부분은 빨간색
        _previewEndGO.SetActive(false);
    }
    #endregion

    #region Input & Update
    private void Update()
    {
        if (!_cam) _cam = Camera.main;

        HandleAltCurveAdjustment();
        HandleInput();
    }

    private void HandleInput()
    {
        if (!BuildModeEnabled) return;

        HandleAltKeyForCurves();
        HandleLeftClick();
        HandlePreview();
        HandleRightClick();
        HandleEscape();
    }

    private void HandleLeftClick()
    {
        if (!Input.GetMouseButtonDown(0)) return;

        if (!_isPreviewing)
            StartPreview();
        else
            BuildRoad();
    }

    private void StartPreview()
    {
        if (!RayToGround(out var hitPos)) return;

        SetStartAnchor(hitPos);
        _isPreviewing = true;
        _bezierReferencePoint = Vector3.zero; // Reset bezier reference point for new preview
        _wasAltPressed = false;
        _previewFirstGO.SetActive(true);
        _previewMidGO.SetActive(true);
        _previewEndGO.SetActive(true);
    }

    private void BuildRoad()
    {
        if (!RayToGround(out var endPos)) return;

        var centerline = CreateCenterline(_startAnchor, endPos, out bool endSnapped);
        if (centerline == null || centerline.Count < 2) return;

        _endSnapped = endSnapped;

        // 새 RoadComponent로 LastRoad 프로퍼티 갱신
        LastRoad = CreateNewRoad(centerline, _startSnapped, _endSnapped);

        // 새 도로의 Cap 상태가 완전히 설정된 후 인접 도로들의 Cap 업데이트
        LastRoad.UpdateCaps();

        SetStartAnchor(endPos);
    }

    private void HandlePreview()
    {
        if (!_isPreviewing || !RayToGround(out var mousePos)) return;

        var centerline = CreateCenterline(_startAnchor, mousePos, out _);
        UpdatePreviewMesh(centerline);
    }

    private void HandleRightClick()
    {
        if (!Input.GetMouseButtonDown(1)) return;

        if (_isPreviewing)
            StopPreview();
        else
            TryDeleteChunkUnderMouse();
    }

    private void HandleEscape()
    {
        if (Input.GetKeyDown(KeyCode.Escape) && _isPreviewing)
            StopPreview();
    }

    private void SetStartAnchor(Vector3 position)
    {
        _startSnapped = TryFindSnap(position, out var sPoint, out var sTan, out var sIsEnd);
        _startAnchor = _startSnapped ? sPoint : position;
    }

    private List<Vector3> CreateCenterline(Vector3 start, Vector3 end, out bool endSnapped)
    {
        bool straight = IsStraightMode();
        bool altCurve = IsAltCurveMode();
        var centerline = BuildCenterlineWithSnap(start, end, straight, altCurve, out _);

        // 끝점 스냅 상태 확인
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
    }

    private void HandleAltCurveAdjustment()
    {
        if (!IsAltPressed()) return;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > SCROLL_THRESHOLD)
        {
            float change = scroll * WHEEL_SENSITIVITY;
            altCurveStrength = Mathf.Max(0f, altCurveStrength + change);
        }
    }

    private bool IsAltPressed() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

    private bool IsStraightMode()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    private bool IsAltCurveMode()
    {
        return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
    }

    private void HandleAltKeyForCurves()
    {
        bool altCurrentlyPressed = IsAltCurveMode();

        // Alt 키가 처음 눌린 순간에 현재 커서 위치를 베지어 참고점으로 저장
        if (altCurrentlyPressed && !_wasAltPressed && _isPreviewing)
        {
            if (RayToGround(out var currentMousePos))
            {
                _bezierReferencePoint = currentMousePos;
            }
        }

        _wasAltPressed = altCurrentlyPressed;
    }

    private bool RayToGround(out Vector3 pos)
    {
        var ray = _cam ? _cam.ScreenPointToRay(Input.mousePosition) : new Ray();
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

        // 기본적으로 직선만 그리기
        if (straightMode || !altCurveMode)
            return BuildStraight(start, end);

        // Alt 키가 눌린 상태에서만 곡선 생성
        if (altCurveMode && _bezierReferencePoint != Vector3.zero)
        {
            return BuildCircularArc(start, end, _bezierReferencePoint);
        }

        return BuildStraight(start, end);
    }

    private static Vector3 AlignToTargetDir(Vector3 hint, Vector3 from, Vector3 to)
    {
        if (hint == Vector3.zero) return Vector3.zero;
        Vector3 d = (to - from);
        if (d.sqrMagnitude < 1e-6f) return hint.normalized;
        return (Vector3.Dot(hint, d) >= 0f) ? hint.normalized : (-hint.normalized);
    }

    private static Vector3 AvoidCoDirectional(Vector3 tangentAtEnd, Vector3 start, Vector3 end)
    {
        if (tangentAtEnd == Vector3.zero) return Vector3.zero;
        Vector3 approach = (start - end);
        if (approach.sqrMagnitude < 1e-6f) return tangentAtEnd.normalized;

        return (Vector3.Dot(tangentAtEnd, approach) > 0f)
            ? (-tangentAtEnd.normalized)
            : (tangentAtEnd.normalized);
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

    private Vector3 ChoosePerpendicular(Vector3 roadTangent, Vector3 desiredDir)
    {
        if (roadTangent == Vector3.zero) return Vector3.zero;
        if (desiredDir == Vector3.zero) return Vector3.Cross(up, roadTangent).normalized;

        var perpL = Vector3.Cross(up, roadTangent).normalized;
        var perpR = -perpL;
        return Vector3.Dot(desiredDir.normalized, perpL) >= Vector3.Dot(desiredDir.normalized, perpR)
            ? perpL : perpR;
    }

    private List<Vector3> BuildBezier(Vector3 a, Vector3 b, Vector3 startDirHint, Vector3 endDirHint)
    {
        var line = new List<Vector3>(64);
        Vector3 ab = b - a;
        float d = Mathf.Max(0.001f, ab.magnitude);

        Vector3 h1 = a + (startDirHint == Vector3.zero ? ab.normalized : startDirHint.normalized)
                        * Mathf.Min(d * handleLenRatio, maxHandleLen);

        Vector3 h2 = b - (endDirHint == Vector3.zero ? ab.normalized : endDirHint.normalized)
                        * Mathf.Min(d * handleLenRatio, maxHandleLen);

        int steps = Mathf.Max(6, Mathf.CeilToInt(d * samplesPerMeter));
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            line.Add(Bezier(a, h1, h2, b, t));
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
        var line = new List<Vector3>(64);

        // 거리 체크
        float distance = Vector3.Distance(start, end);
        if (distance < 0.001f) return BuildStraight(start, end);

        // 참조점에서 현재 커서 위치로의 변위
        Vector3 referenceToEnd = end - referencePoint;

        // 커서가 참조점에서 움직이지 않았다면 직선 반환
        if (referenceToEnd.sqrMagnitude < 0.01f)
        {
            return BuildStraight(start, referencePoint);
        }

        // 전체 곡선 생성: 시작점에서 현재 커서 위치까지
        // 참조점은 곡선의 제어점 역할
        return BuildElasticCurve(start, end, referencePoint);
    }

    private List<Vector3> BuildElasticCurve(Vector3 start, Vector3 currentEnd, Vector3 referencePoint)
    {
        var line = new List<Vector3>(64);

        // 시작점, 참조점, 커서 위치를 지나는 자연스러운 곡선 생성
        // 3점을 지나는 2차 베지어 곡선 또는 3차 베지어 곡선 사용

        // 시작점에서 참조점으로의 방향
        Vector3 startToRef = (referencePoint - start).normalized;
        float startToRefDist = Vector3.Distance(start, referencePoint);

        // 참조점에서 커서로의 방향
        Vector3 refToCursor = (currentEnd - referencePoint).normalized;
        float refToCursorDist = Vector3.Distance(referencePoint, currentEnd);

        // 전체 거리
        float totalDist = Vector3.Distance(start, currentEnd);

        // 참조점을 정확히 지나가는 3차 베지어 곡선 생성
        // P0 = start, P1 = control1, P2 = control2, P3 = currentEnd
        // 그리고 t=0.5일 때 참조점을 지나가도록 조정

        // 첫 번째 컨트롤 포인트: 시작점 방향 고정 (항상 참조점 방향)
        float controlStrength1 = startToRefDist * 0.4f;
        Vector3 control1 = start + startToRef * controlStrength1;

        // 두 번째 컨트롤 포인트: 길이 비율에 따른 끝점 방향 계산
        float startToCursorDist = Vector3.Distance(start, currentEnd);
        float lengthRatio = startToCursorDist / startToRefDist;

        Vector3 endTangentDirection;

        // 길이 비율에 따른 끝점 탄젠트 방향 결정
        if (Mathf.Abs(lengthRatio - 1.0f) < 0.1f)
        {
            // 길이가 거의 같으면 (커서B 케이스): 시작점 방향과 수평
            endTangentDirection = startToRef;
        }
        else
        {
            // 참조점에서 커서로의 방향
            Vector3 refToCursorNorm = refToCursor.normalized;

            // 참조점-커서 거리와 참조점-시작점 거리 비교
            float refCursorRatio = refToCursorDist / startToRefDist;

            if (Mathf.Abs(refCursorRatio - 1.0f) < 0.1f)
            {
                // 참조점-커서 거리가 참조점-시작점과 거의 같으면 (커서A 케이스): 참조점과 수직
                Vector3 perpendicular = Vector3.Cross(up, startToRef).normalized;
                endTangentDirection = Vector3.Dot(refToCursorNorm, perpendicular) > 0 ? perpendicular : -perpendicular;
            }
            else
            {
                // 길이 비율에 따라 방향 조절
                Vector3 baseDirection = startToRef; // 수평 기준
                Vector3 perpendicular = Vector3.Cross(up, startToRef).normalized;

                // 길이가 길어질수록 바깥쪽으로, 짧아질수록 안쪽으로
                float bendFactor = (lengthRatio - 1.0f) * 0.5f; // -0.5 ~ 0.5 범위

                // 커서가 어느 쪽에 있는지 확인
                Vector3 startToCursor = (currentEnd - start).normalized;
                float crossProduct = Vector3.Dot(Vector3.Cross(startToRef, startToCursor), up);
                bool isRightSide = crossProduct > 0;

                Vector3 bendDirection = isRightSide ? perpendicular : -perpendicular;
                endTangentDirection = (baseDirection + bendDirection * bendFactor).normalized;
            }
        }

        float controlStrength2 = refToCursorDist * 0.4f;
        Vector3 control2 = currentEnd - endTangentDirection * controlStrength2;

        // 참조점을 지나가도록 두 번째 컨트롤 포인트만 조정
        // 베지어 곡선에서 t=0.5일 때 참조점이 나오도록 역계산
        Vector3 midPointBezier = 0.125f * start + 0.375f * control1 + 0.375f * control2 + 0.125f * currentEnd;
        Vector3 offset = referencePoint - midPointBezier;

        // 시작점 방향을 유지하기 위해 두 번째 컨트롤 포인트만 조정
        control2 += offset;

        // 베지어 곡선 생성
        int steps = Mathf.Max(8, Mathf.CeilToInt(totalDist * samplesPerMeter));

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 point = Bezier(start, control1, control2, currentEnd, t);
            line.Add(point);
        }

        return line;
    }

    private Vector3 RotateVectorAroundAxis(Vector3 vector, Vector3 axis, float angle)
    {
        return Quaternion.AngleAxis(angle * Mathf.Rad2Deg, axis) * vector;
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

        // 센터라인을 세 부분으로 나누기
        var segments = DivideCenterlineIntoThreeSegments(centerline);

        // First segment
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

        // Mid segment
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

        // End segment
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

        // Mid는 전체 centerLine을 사용
        var mid = new List<Vector3>(centerline);

        // First: centerLine 시작점 앞쪽 연장선
        var first = CreateExtensionSegment(centerline, true, capLength);

        // End: centerLine 끝점 뒤쪽 연장선
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
            // 시작점에서 첫 번째 방향의 반대로 연장
            direction = (centerline[0] - centerline[1]).normalized;
            startPoint = centerline[0];
        }
        else
        {
            // 끝점에서 마지막 방향으로 연장
            direction = (centerline[centerline.Count - 1] - centerline[centerline.Count - 2]).normalized;
            startPoint = centerline[centerline.Count - 1];
        }

        // 연장선 세그먼트 생성
        var extension = new List<Vector3>();

        if (isStart)
        {
            // First: 연장점 → 시작점
            Vector3 extensionPoint = startPoint + direction * length;
            extension.Add(extensionPoint);
            extension.Add(startPoint);
        }
        else
        {
            // End: 끝점 → 연장점
            extension.Add(startPoint);
            Vector3 extensionPoint = startPoint + direction * length;
            extension.Add(extensionPoint);
        }

        return extension;
    }
    #endregion

    #region Road Generation & Management
    private RoadComponent CreateNewRoad(List<Vector3> centerline, bool startConnected, bool endConnected)
    {
        var roadGO = GetRoadFromPool();
        var road = roadGO.GetComponent<RoadComponent>();

        var dirStart = DirectionAtStart(centerline);
        var dirEnd = DirectionAtEnd(centerline);

        // Active 상태로 초기화
        road.Initialize(centerline, dirStart, dirEnd, roadWidth, startConnected, endConnected, RoadState.Active);

        // 청크들 생성
        CreateChunksUnderRoad(road, centerline, roadGO.transform, false);

        // Cap 생성
        CreateCapsForRoad(road, roadGO.transform);

        // 활성 도로 목록에 추가
        _roads.Add(road);

        // 새 도로의 Cap 상태 설정
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

                // next seed
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

        // 프리뷰인 경우 머티리얼 색상 변경
        if (isPreview)
        {
            SetMaterialColor(mr, previewColor);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }
        else
        {
            // 일반 도로인 경우 원래 머티리얼 속성 복원
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

        var ray = _cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out var hit, rayMaxDistance, ~0, QueryTriggerInteraction.Collide)) return;

        var go = hit.collider ? hit.collider.gameObject : null;
        if (!go) return;

        // RoadChunk 삭제 시도
        for (int i = 0; i < _chunks.Count; i++)
        {
            if (_chunks[i].go == go)
            {
                _chunks.RemoveAt(i);
                var road = go.GetComponentInParent<RoadComponent>();
                if (road)
                {

                    // TODO(human): 도로의 모든 청크가 삭제되었는지 확인하고,
                    // 모든 청크가 삭제되었다면 road.NotifyDeletion() 호출하여
                    // 인접 도로들과의 관계를 정리해주세요

                    // 도로 삭제 시 인접 도로들의 Cap 업데이트
                    road.UpdateCaps();
                }
                Destroy(go);
                return;
            }
        }

        // RoadCap 삭제 시도
        for (int i = 0; i < _caps.Count; i++)
        {
            if (_caps[i].go == go)
            {
                var road = go.GetComponentInParent<RoadComponent>();

                _caps.RemoveAt(i);

                // RoadComponent에서도 Cap 청크 제거
                if (road != null)
                {

                }

                Destroy(go);
                return;
            }
        }
    }
    #endregion

    #region Snapping
    private bool TryFindSnap(Vector3 query, out Vector3 snapPoint, out Vector3 snapTangent, out bool isEndpoint, RoadComponent excludeRoad = null)
    {
        snapPoint = default;
        snapTangent = default;
        isEndpoint = false;

        float bestDist = float.MaxValue;

        // Physics.OverlapSphere로 Road 레이어에서 탐색
        int layerMask = 1 << gameObject.layer;

        Collider[] colliders = Physics.OverlapSphere(query, snapDistance, layerMask);

        var checkedRoads = new HashSet<RoadComponent>();

        foreach (var collider in colliders)
        {
            if (collider == null) continue;

            // RoadComponent 찾기 (부모에서 탐색)
            var road = collider.GetComponentInParent<RoadComponent>();
            if (road == null || road.Centerline == null || road.Centerline.Count < 2) continue;

            // 같은 RoadComponent의 청크는 제외 (문서 명세에 따라)
            if (excludeRoad != null && road == excludeRoad) continue;

            // Cap이 아닌 RoadChunk인지 확인 (collider 이름으로 구분)
            if (collider.name.Contains("Cap")) continue;

            // 이미 확인한 도로는 중복 체크 방지
            if (checkedRoads.Contains(road)) continue;
            checkedRoads.Add(road);

            if (ClosestPointOnPolyline(road.Centerline, query, out var p, out float d))
            {
                if (d < bestDist)
                {
                    bestDist = d;
                    snapPoint = p;
                    // 간단한 방향 계산
                    snapTangent = Vector3.forward;
                }
            }
        }

        return bestDist <= snapDistance;
    }

    private static bool ClosestPointOnPolyline(List<Vector3> centerlineVertices, Vector3 query, out Vector3 closestPoint, out float distance)
    {
        closestPoint = default;
        distance = float.MaxValue;

        if (centerlineVertices == null || centerlineVertices.Count < 1) return false;

        // centerline vertex 중 가장 가까운 점 찾기
        for (int i = 0; i < centerlineVertices.Count; i++)
        {
            float d = Vector3.Distance(query, centerlineVertices[i]);
            if (d < distance)
            {
                distance = d;
                closestPoint = centerlineVertices[i];
            }
        }

        return distance < float.MaxValue;
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

        // 시작점 cap 또는 끝점 cap 버텍스
        verts[0] = startLeft;
        verts[1] = startRight;
        verts[2] = endLeft;
        verts[3] = endRight;

        // UV 매핑 (간단한 0-1 범위)
        uvs[0] = new Vector2(0, 0);
        uvs[1] = new Vector2(1, 0);
        uvs[2] = new Vector2(0, 1);
        uvs[3] = new Vector2(1, 1);

        // 두 개의 삼각형으로 사각형 구성
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
            // Front Cap 생성 (시작점이 연결되지 않은 경우)
            road.ChunksCap_First = CreateCapForRoad(
                road.LeftEdgeLine[0], road.RightEdgeLine[0],
                road.LeftEdgeLine[1], road.RightEdgeLine[1],
                road, "FrontCap", parent
                );

            // End Cap 생성 (끝점이 연결되지 않은 경우)
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

        // Cap 추적을 위해 리스트에 추가
        _caps.Add(new RoadCapRef { go = capGO, mesh = mesh });
        return capGO;
    }

    private void ReturnCapToPool(GameObject capGO)
    {
        if (capGO == null) return;

        // _caps 리스트에서 제거
        _caps.RemoveAll(c => c.go == capGO);

        // 풀로 반환
        ReturnChunkToPool(capGO);
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
