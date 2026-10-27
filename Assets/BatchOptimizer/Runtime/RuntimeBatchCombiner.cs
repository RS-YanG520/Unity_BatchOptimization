using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BatchOptimizer
{
    /// <summary>运行时合批的对象收集方式。</summary>
    public enum RuntimeCollectMode
    {
        /// <summary>收集本对象下的子物体（配合 onlyTagged）。</summary>
        Children,

        /// <summary>收集场景中所有静态对象（isStatic）。</summary>
        StaticInScene,

        /// <summary>收集场景中所有带 BatchTag 的对象。</summary>
        TaggedInScene,
    }

    /// <summary>
    /// 运行时合批器：在 Awake 时把子对象中「网格 + 材质」完全相同的对象合并成单个网格，
    /// 用于运行时动态生成、之后不再各自移动的对象，减少 Draw Call。
    /// 注意：合并后这些对象将作为一个整体渲染，无法再单独移动 / 隐藏。
    /// </summary>
    [DisallowMultipleComponent]
    public class RuntimeBatchCombiner : MonoBehaviour
    {
        [Tooltip("是否在 Awake 自动执行合批")]
        public bool combineOnAwake = true;

        [Tooltip("收集对象的方式：Children=子物体；StaticInScene=场景中所有静态对象；TaggedInScene=场景中所有带 BatchTag 的对象")]
        public RuntimeCollectMode collectMode = RuntimeCollectMode.Children;

        [Tooltip("仅当 collectMode=Children 时生效：true 只收集带 BatchTag 的子物体，false 收集所有子物体")]
        public bool onlyTagged = true;

        [Tooltip("单个合并网格的顶点上限，超过会自动分块")]
        public int maxVerticesPerMesh = 65535;

        [Tooltip("是否保留光照贴图信息（uv2 / scaleOffset）")]
        public bool preserveLightmap = true;

        [Tooltip("true 禁用整个原始对象；false 仅禁用其 Renderer")]
        public bool disableWholeObject = false;

        private bool _combined;

        private void Awake()
        {
            if (combineOnAwake) CombineNow();
        }

        /// <summary>立即执行合批。可手动调用。</summary>
        public void CombineNow()
        {
            if (_combined)
            {
                BatchLogger.Warn("RuntimeBatchCombiner[" + name + "] 已执行过合批，跳过重复调用");
                return;
            }
            _combined = true;

            var candidates = new List<Renderer>();
            CollectCandidates(transform, candidates);

            if (candidates.Count == 0)
            {
                BatchLogger.Info("RuntimeBatchCombiner[" + name + "]：没有符合条件的子对象");
                return;
            }

            var groups = MeshBatchCombiner.ScanAndGroup(candidates, preserveLightmap, minGroupCount: 2);
            if (groups.Count == 0)
            {
                BatchLogger.Info("RuntimeBatchCombiner[" + name + "]：没有可合批的分组");
                return;
            }

            int before = 0;
            int after = 0;
            int combined = 0;
            int probeDependent = 0;

            foreach (var g in groups)
            {
                before += g.OriginalDrawCalls;
                var results = MeshBatchCombiner.CombineGroup(g, transform, maxVerticesPerMesh, preserveLightmap);
                foreach (var res in results)
                {
                    MeshBatchCombiner.CreateCombinedObject(res, transform, disableWholeObject);
                    if (res.sourceRenderers.Count > 0)
                    {
                        Renderer src = res.sourceRenderers[0];
                        if (src.lightmapIndex < 0 && src.lightProbeUsage != LightProbeUsage.Off)
                            probeDependent++;
                    }
                    after += res.materials.Length;
                    combined++;
                }
            }

            BatchLogger.Info("RuntimeBatchCombiner[" + name + "] 合批完成：Draw Call " + before + " -> " + after +
                             "（减少 " + (before - after) + "），生成 " + combined + " 个合并网格");

            if (probeDependent > 0)
                BatchLogger.Warn("RuntimeBatchCombiner[" + name + "]：有 " + probeDependent +
                                 " 个合并结果依赖光照探针（源对象无光照贴图），合并后探针采样位置可能与原位置不一致");
        }

        private void CollectCandidates(Transform root, List<Renderer> list)
        {
            if (collectMode == RuntimeCollectMode.StaticInScene)
            {
                var all = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (var mr in all)
                {
                    if (mr == null || !mr.enabled) continue;
                    if (mr.GetComponentInParent<BatchCombinedResult>() != null) continue;
                    if (mr.gameObject.isStatic) list.Add(mr);
                }
                return;
            }

            if (collectMode == RuntimeCollectMode.TaggedInScene)
            {
                var all = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (var mr in all)
                {
                    if (mr == null || !mr.enabled) continue;
                    if (mr.GetComponentInParent<BatchCombinedResult>() != null) continue;

                    BatchTag tag = mr.GetComponent<BatchTag>();
                    if (tag != null && tag.includeInBatching)
                        list.Add(mr);
                }
                return;
            }

            // Children 模式：收集本对象的子物体
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);

                // 跳过已经生成的合并结果对象，避免二次合批
                if (child.GetComponent<BatchCombinedResult>() != null) continue;

                if (onlyTagged)
                {
                    BatchTag tag = child.GetComponent<BatchTag>();
                    if (tag != null && tag.includeInBatching)
                    {
                        MeshRenderer mr = child.GetComponent<MeshRenderer>();
                        if (mr != null) list.Add(mr);
                    }
                }
                else
                {
                    MeshRenderer mr = child.GetComponent<MeshRenderer>();
                    if (mr != null) list.Add(mr);
                }

                CollectCandidates(child, list);
            }
        }
    }
}
