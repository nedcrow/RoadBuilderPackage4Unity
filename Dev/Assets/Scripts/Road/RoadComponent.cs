using System.Collections.Generic;
using UnityEngine;

public enum RoadState
{
    Preview,
    Active,
    Pooled
}

public class RoadComponent : MonoBehaviour
{
    [SerializeField] private float width;
    [SerializeField] private List<Vector3> centerline = new();
    [SerializeField] private List<Vector3> leftEdgeLine = new();
    [SerializeField] private List<Vector3> rightEdgeLine = new();
    [SerializeField] private Vector3 dirStart;
    [SerializeField] private Vector3 dirEnd;

    [Header("Road Connection Status")]
    [SerializeField] private bool frontCap = false;
    [SerializeField] private bool endCap = false;
    [SerializeField] private RoadState state = RoadState.Active;

    private readonly HashSet<RoadComponent> _nearbyRoads = new();
    public List<Vector3> Centerline => centerline;
    public List<Vector3> LeftEdgeLine => leftEdgeLine;
    public List<Vector3> RightEdgeLine => rightEdgeLine;
    public float Width => width;
    public GameObject ChunksCap_First;
    public GameObject ChunksCap_End;
    public int ChunkCounter = 0;
    public bool FrontCap => frontCap;
    public bool EndCap => endCap;
    public RoadState State => state;
    public HashSet<RoadComponent> NearbyRoads => _nearbyRoads;

    public void Initialize(List<Vector3> line, Vector3 startDir, Vector3 endDir, float w, bool startConn = false, bool endConn = false, RoadState roadState = RoadState.Active)
    {
        width = w;
        centerline = new List<Vector3>(line);
        dirStart = startDir;
        dirEnd = endDir;
        frontCap = startConn;
        endCap = endConn;
        state = roadState;

        // 테두리 라인 생성
        GenerateEdgeLines();
    }

    public void SetState(RoadState newState)
    {
        state = newState;
    }

    #region Caps Management
    // 인접 RoadComponent와 스스로의 Cap 활성화/비활성화
    public void UpdateCaps()
    {
        // FrontCap
        Vector3 firstPoint = centerline[0];
        Vector3 endPoint = centerline[centerline.Count - 1];
        RoadComponent nearest_First = FindNearestRoadFrom(firstPoint);
        if (nearest_First == null)
        {
            OnCap_Front();
        }
        else
        {
            OffCap_Front();
            AddNearbyRoad(nearest_First);
            nearest_First.AddNearbyRoad(this);
            if (firstPoint == nearest_First.centerline[0])
            {
                nearest_First.OffCap_Front();
            }
            else if (firstPoint == nearest_First.centerline[nearest_First.centerline.Count - 1])
            {
                nearest_First.OffCap_End();
            }
        }

        // EndCap
        RoadComponent nearest_End = FindNearestRoadFrom(endPoint);
        if (nearest_End == null)
        {
            OnCap_End();
        }
        else
        {
            OffCap_End();
            AddNearbyRoad(nearest_End);
            nearest_End.AddNearbyRoad(this);
            if (endPoint == nearest_End.centerline[0])
            {
                nearest_End.OffCap_Front();
            }
            else if (endPoint == nearest_End.centerline[nearest_End.centerline.Count - 1])
            {
                nearest_End.OffCap_End();
            }
        }
    }

    public void OnCap_Front()
    {
        frontCap = true;
        if (ChunksCap_First != null) ChunksCap_First.SetActive(true);
    }

    public void OnCap_End()
    {
        endCap = true;
        if (ChunksCap_End != null) ChunksCap_End.SetActive(true);
    }

    public void OffCap_Front()
    {
        frontCap = false;
        if (ChunksCap_First != null) ChunksCap_First.SetActive(false);
    }

    public void OffCap_End()
    {
        endCap = false;
        if (ChunksCap_End != null) ChunksCap_End.SetActive(false);
    }
    #endregion

    #region Generate Lines(center, edge, cap)
    /// <summary>
    /// centerline을 기반으로 좌우 테두리 라인을 생성
    /// miter join을 사용하여 코너에서의 겹침/간격 문제 해결
    /// 양 끝에 end cap 추가로 완전히 닫힌 도로 생성 (centerline 길이는 유지)
    /// </summary>
    private void GenerateEdgeLines()
    {
        if (centerline == null || centerline.Count < 2)
        {
            leftEdgeLine.Clear();
            rightEdgeLine.Clear();
            return;
        }

        leftEdgeLine.Clear();
        rightEdgeLine.Clear();

        float halfWidth = width * 0.5f;

        // centerline 기반으로 기본 edge line 생성
        for (int i = 0; i < centerline.Count; i++)
        {
            Vector3 perpendicular;

            if (i == 0)
            {
                // 첫 번째 점: 다음 점과의 방향 사용
                Vector3 forward = (centerline[1] - centerline[0]).normalized;
                perpendicular = new Vector3(-forward.z, 0, forward.x);
            }
            else if (i == centerline.Count - 1)
            {
                // 마지막 점: 이전 점과의 방향 사용
                Vector3 forward = (centerline[i] - centerline[i - 1]).normalized;
                perpendicular = new Vector3(-forward.z, 0, forward.x);
            }
            else
            {
                // 중간 점: miter join 계산
                Vector3 prevForward = (centerline[i] - centerline[i - 1]).normalized;
                Vector3 nextForward = (centerline[i + 1] - centerline[i]).normalized;

                Vector3 prevPerpendicular = new Vector3(-prevForward.z, 0, prevForward.x);
                Vector3 nextPerpendicular = new Vector3(-nextForward.z, 0, nextForward.x);

                // miter 벡터 계산
                Vector3 miter = (prevPerpendicular + nextPerpendicular).normalized;

                // miter 길이 계산 (각도에 따라 조정)
                float miterLength = halfWidth / Vector3.Dot(miter, prevPerpendicular);

                // 너무 긴 miter 제한 (예각에서 발생)
                float maxMiterLength = halfWidth * 3f;
                if (Mathf.Abs(miterLength) > maxMiterLength)
                {
                    miterLength = Mathf.Sign(miterLength) * maxMiterLength;
                }

                perpendicular = miter * (miterLength / halfWidth);
            }

            // 좌우 테두리 점 계산
            Vector3 leftPoint = centerline[i] + perpendicular * halfWidth;
            Vector3 rightPoint = centerline[i] - perpendicular * halfWidth;

            leftEdgeLine.Add(leftPoint);
            rightEdgeLine.Add(rightPoint);
        }

        // 시작점 end cap 추가 (연결되지 않은 경우만)
        if (!frontCap)
        {
            Vector3 startDirection = (centerline[1] - centerline[0]).normalized;
            Vector3 startPerpendicular = new Vector3(-startDirection.z, 0, startDirection.x);
            float capExtension = halfWidth;

            Vector3 startCapCenter = centerline[0] - startDirection * capExtension;
            Vector3 startCapLeft = startCapCenter + startPerpendicular * halfWidth;
            Vector3 startCapRight = startCapCenter - startPerpendicular * halfWidth;

            leftEdgeLine.Insert(0, startCapLeft);
            rightEdgeLine.Insert(0, startCapRight);
        }

        // 끝점 end cap 추가 (연결되지 않은 경우만)
        if (!endCap)
        {
            Vector3 endDirection = (centerline[centerline.Count - 1] - centerline[centerline.Count - 2]).normalized;
            Vector3 endPerpendicular = new Vector3(-endDirection.z, 0, endDirection.x);
            float capExtension = halfWidth;

            Vector3 endCapCenter = centerline[centerline.Count - 1] + endDirection * capExtension;
            Vector3 endCapLeft = endCapCenter + endPerpendicular * halfWidth;
            Vector3 endCapRight = endCapCenter - endPerpendicular * halfWidth;

            leftEdgeLine.Add(endCapLeft);
            rightEdgeLine.Add(endCapRight);
        }
    }
    #endregion

    #region Nearby Roads Management
    /// <summary>
    /// 인접 RoadComponent 중 해당 위치에 시작, 끝점을 가진 RoadComponent 반환.
    /// </summary>
    private RoadComponent FindNearestRoadFrom(Vector3 point)
    {
        // Physics.OverlapSphere로 근처의 도로 청크들 찾기
        int layerMask = 1 << gameObject.layer;
        Collider[] colliders = Physics.OverlapSphere(point, 0.1f, layerMask);

        RoadComponent result = null;
        foreach (var collider in colliders)
        {
            if (collider == null) continue;

            // RoadComponent 찾기
            var road = collider.GetComponentInParent<RoadComponent>();
            if (road == null) continue;
            if (road == this) continue;
            if (road.Centerline == null || road.Centerline.Count < 2) continue;

            // Cap이 아닌 실제 도로 청크인지 확인
            if (collider.name.Contains("Cap")) continue;
            if (road.centerline.Contains(point)) return road;
        }
        return result;
    }


    /// <summary>
    /// 인접 도로를 추가합니다. 중복 방지됩니다.
    /// </summary>
    public void AddNearbyRoad(RoadComponent road)
    {
        if (road != null && road != this)
        {
            _nearbyRoads.Add(road);
        }
    }

    /// <summary>
    /// 인접 도로를 제거합니다.
    /// </summary>
    public void RemoveNearbyRoad(RoadComponent road)
    {
        if (road != null)
        {
            _nearbyRoads.Remove(road);
        }
    }

    /// <summary>
    /// 이 도로가 삭제될 때 호출되어 모든 인접 도로들에게 자신을 제거하라고 알립니다.
    /// </summary>
    public void NotifyDeletion()
    {
        // 복사본을 만들어서 순회 (컬렉션 수정 중 순회 방지)
        var nearbyRoadsCopy = new HashSet<RoadComponent>(_nearbyRoads);

        foreach (var nearbyRoad in nearbyRoadsCopy)
        {
            if (nearbyRoad != null)
            {
                nearbyRoad.RemoveNearbyRoad(this);
            }
        }

        _nearbyRoads.Clear();
    }

    /// <summary>
    /// 모든 인접 도로 관계를 정리합니다.
    /// </summary>
    public void ClearNearbyRoads()
    {
        _nearbyRoads.Clear();
    }
    #endregion
}
