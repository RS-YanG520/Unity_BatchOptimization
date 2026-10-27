using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BatchOptimizer
{
    /// <summary>合批对象来源过滤方式。</summary>
    public enum BatchIncludeMode
    {
        /// <summary>仅 Unity 静态（Batching Static）对象。</summary>
        StaticFlag,

        /// <summary>仅带 BatchTag 标记的对象。</summary>
        BatchTag,

        /// <summary>满足其一即可。</summary>
        Both,
    }

    /// <summary>合批执行方式。</summary>
    public enum BatchExecutionMode
    {
        /// <summary>静态合批：编辑器里合并，结果存成 .asset 资源文件。</summary>
        StaticAsset,

        /// <summary>运行时合批：Awake 时合并，纯内存，不生成资源文件。</summary>
        RuntimeMemory,
    }

    /// <summary>
    /// 编辑器静态合批窗口。
    /// 扫描场景中符合条件、且「网格 + 材质」完全相同的对象，合并成单个网格以降低 Draw Call。
    /// </summary>
    public class StaticBatchCombinerWindow : EditorWindow
    {
        private BatchIncludeMode includeMode = BatchIncludeMode.Both;
        private BatchExecutionMode executionMode = BatchExecutionMode.StaticAsset;
        private int maxVerticesPerMesh = 65535;
        private int minGroupCount = 2;
        private bool preserveLightmap = true;
        private bool groupByLightmap = true;
        private bool disableWholeObject = false;

        private List<BatchGroup> _groups = new List<BatchGroup>();
        private int _candidateCount;
        private int _beforeDrawCalls;
        private int _afterDrawCalls;
        private int _combinedMeshCount;
        private bool _hasScan;
        private Vector2 _scroll;

        [MenuItem("Tools/Batch Optimizer/合批窗口 (Batch)")]
        public static void Open()
        {
            GetWindow<StaticBatchCombinerWindow>("合批优化器");
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("合批优化器 —— 只把「网格 + 材质」完全相同的模型合并到一起", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "与 Unity 内置静态合批不同，本工具按 (Mesh + 材质数组 + 光照贴图索引) 精确分组，\n" +
                "避免把不同模型或不同材质错误合并，导致 Draw Call 反而升高。", MessageType.Info);

            EditorGUILayout.Space();
            executionMode = (BatchExecutionMode)EditorGUILayout.EnumPopup(
                new GUIContent("合批方式", "StaticAsset = 编辑器里合并、结果存成 .asset 资源；RuntimeMemory = 运行时收集场景静态对象合并、纯内存不落盘。"),
                executionMode);
            includeMode = (BatchIncludeMode)EditorGUILayout.EnumPopup(
                new GUIContent("筛选对象方式", "StaticFlag 只处理勾选 Batching Static 的对象；BatchTag 只处理带 BatchTag 的对象；Both 满足其一即可。"),
                includeMode);
            maxVerticesPerMesh = EditorGUILayout.IntField(
                new GUIContent("单网格顶点上限", "合并后单网格顶点超过该值会自动分块成多个网格（默认 65535）。"),
                maxVerticesPerMesh);
            minGroupCount = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("最少对象数量才合并", "分组内相同模型的数量少于该值时不合并。"),
                minGroupCount));
            preserveLightmap = EditorGUILayout.Toggle(
                new GUIContent("保留光照贴图 (uv2/scaleOffset)", "把每个对象的 uv2 换算到光照贴图图集空间，使合并后光照正确。"),
                preserveLightmap);
            groupByLightmap = EditorGUILayout.Toggle(
                new GUIContent("按光照贴图索引分组", "不同光照贴图图集的对象不合并，避免光照贴图错乱。"),
                groupByLightmap);
            disableWholeObject = EditorGUILayout.Toggle(
                new GUIContent("禁用整个原始对象", "勾选 = 禁用整个 GameObject；不勾选 = 仅禁用 Renderer。"),
                disableWholeObject);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("1. 扫描场景", "扫描当前场景，按 (网格 + 材质 + 光照贴图索引) 分组并预估 Draw Call。"), GUILayout.Height(28))) Scan();

            GUI.enabled = _hasScan && _groups.Count > 0;
            string actionLabel = executionMode == BatchExecutionMode.StaticAsset
                ? "2. 执行合批（静态，存资源）"
                : "2. 准备运行时合批（内存）";
            string actionTooltip = executionMode == BatchExecutionMode.StaticAsset
                ? "编辑器里合并相同模型，结果存成资源文件。"
                : "创建运行时合批器，运行时收集场景静态对象自动合并（纯内存）。";
            if (GUILayout.Button(new GUIContent(actionLabel, actionTooltip), GUILayout.Height(28))) Combine();
            GUI.enabled = true;

            if (GUILayout.Button(new GUIContent("撤销合批 (Revert)", "恢复原始对象并删除合并结果。"), GUILayout.Height(28))) Revert();
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button(new GUIContent("打开日志文件", "在文件管理器中定位日志文件 Logs/BatchOptimizer.log。"), GUILayout.Height(22)))
            {
                EditorUtility.RevealInFinder(BatchLogger.GetLogPath());
            }

            EditorGUILayout.Space();
            if (_hasScan)
            {
                EditorGUILayout.LabelField(
                    "候选对象 " + _candidateCount +
                    "，可合批分组 " + _groups.Count +
                    "，预计 Draw Call " + _beforeDrawCalls + " -> " + _afterDrawCalls +
                    "（减少 " + (_beforeDrawCalls - _afterDrawCalls) + "），合并网格数 " + _combinedMeshCount);

                EditorGUILayout.Space();
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var g in _groups)
                {
                    int chunks = MeshBatchCombiner.EstimateCombinedMeshCount(g, maxVerticesPerMesh);
                    EditorGUILayout.LabelField(
                        "- 「" + g.sharedMesh.name + "」 x" + g.renderers.Count +
                        "，材质 " + g.sharedMaterials.Length +
                        "，DrawCall " + g.OriginalDrawCalls + " -> " + (chunks * g.SubMeshCount));
                }
                EditorGUILayout.EndScrollView();
            }
            else
            {
                EditorGUILayout.HelpBox("点击「扫描场景」查看可合批的分组。", MessageType.None);
            }
        }

        private void Scan()
        {
            var all = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var candidates = new List<Renderer>();
            int skipped = 0;

            foreach (var mr in all)
            {
                if (mr == null) continue;

                // 跳过已经被合批过的对象（自身是合并结果，或已被禁用）
                if (!mr.enabled) { skipped++; continue; }
                if (mr.GetComponentInParent<BatchCombinedResult>() != null) { skipped++; continue; }

                // 运行时合批收集的是「静态对象」，用 isStatic 过滤，与运行时收集保持一致
                bool pass = executionMode == BatchExecutionMode.RuntimeMemory
                    ? mr.gameObject.isStatic
                    : PassesIncludeFilter(mr, includeMode);

                if (pass)
                    candidates.Add(mr);
                else
                    skipped++;
            }

            _groups = MeshBatchCombiner.ScanAndGroup(candidates, groupByLightmap, minGroupCount);
            _candidateCount = candidates.Count;
            ComputeStats();
            _hasScan = true;

            BatchLogger.Info("场景扫描：MeshRenderer 总数 " + all.Length + "，候选 " + candidates.Count + "，跳过 " + skipped);
            Repaint();
        }

        private void Combine()
        {
            if (executionMode == BatchExecutionMode.RuntimeMemory)
            {
                PrepareRuntimeBatch();
                return;
            }

            if (!_hasScan || _groups.Count == 0)
            {
                Scan();
                if (_groups.Count == 0)
                {
                    EditorUtility.DisplayDialog("合批", "没有可合批的分组。", "确定");
                    return;
                }
            }

            BatchLogger.Info("========== 开始静态合批 ==========");

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Batch Combine");

            var batchRoot = new GameObject("BatchOptimizer_Root");
            Undo.RegisterCreatedObjectUndo(batchRoot, "Batch Combine");
            batchRoot.transform.SetAsLastSibling();

            int before = 0;
            int after = 0;
            int combined = 0;

            foreach (var g in _groups)
            {
                before += g.OriginalDrawCalls;

                // 记录原始对象状态，便于 Ctrl+Z 撤销
                foreach (var r in g.renderers)
                    Undo.RegisterFullObjectHierarchyUndo(r.gameObject, "Batch Combine");

                var results = MeshBatchCombiner.CombineGroup(g, batchRoot.transform, maxVerticesPerMesh, preserveLightmap);
                foreach (var res in results)
                {
                    SaveMeshAsset(res.combinedMesh);

                    var go = MeshBatchCombiner.CreateCombinedObject(res, batchRoot.transform, disableWholeObject);
                    Undo.RegisterCreatedObjectUndo(go, "Batch Combine");
                    combined++;
                    after += res.materials.Length;
                }
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            BatchLogger.Info("========== 合批完成：Draw Call " + before + " -> " + after +
                             "（减少 " + (before - after) + "），生成 " + combined + " 个合并网格 ==========");

            _hasScan = false;
            _groups.Clear();

            EditorUtility.DisplayDialog("合批完成",
                "Draw Call：" + before + " -> " + after +
                "\n减少 " + (before - after) + " 个 Draw Call，生成 " + combined + " 个合并网格。" +
                "\n详细过程见 Console 与 Logs/BatchOptimizer.log。", "确定");
        }

        private void PrepareRuntimeBatch()
        {
            if (!_hasScan || _groups.Count == 0)
            {
                Scan();
                if (_groups.Count == 0)
                {
                    EditorUtility.DisplayDialog("合批", "没有可合批的分组。", "确定");
                    return;
                }
            }

            BatchLogger.Info("========== 准备运行时合批（收集静态对象，纯内存，不落盘） ==========");

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Setup Runtime Batch");

            // 找到或创建运行时合批器（不挂 BatchTag，运行时收集场景静态对象）
            var root = GameObject.Find("RuntimeBatchCombiner_Root");
            if (root == null)
            {
                root = new GameObject("RuntimeBatchCombiner_Root");
                Undo.RegisterCreatedObjectUndo(root, "Setup Runtime Batch");
            }

            var combiner = root.GetComponent<RuntimeBatchCombiner>();
            if (combiner == null)
                combiner = Undo.AddComponent<RuntimeBatchCombiner>(root);

            combiner.collectMode = RuntimeCollectMode.StaticInScene;
            combiner.combineOnAwake = true;
            combiner.maxVerticesPerMesh = maxVerticesPerMesh;
            combiner.preserveLightmap = preserveLightmap;
            combiner.disableWholeObject = disableWholeObject;

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            int before = 0;
            int after = 0;
            int combined = 0;
            foreach (var g in _groups)
            {
                before += g.OriginalDrawCalls;
                int chunks = MeshBatchCombiner.EstimateCombinedMeshCount(g, maxVerticesPerMesh);
                after += chunks * g.SubMeshCount;
                combined += chunks;
            }

            BatchLogger.Info("========== 运行时合批准备完成：运行后收集场景静态对象（isStatic），预计 Draw Call " + before + " -> " + after + "（生成 " + combined + " 个合并网格） ==========");

            _hasScan = false;
            _groups.Clear();

            EditorUtility.DisplayDialog("运行时合批",
                "已创建/配置 RuntimeBatchCombiner_Root（collectMode = StaticInScene）。\n" +
                "运行时 Awake 会收集场景中所有静态对象（isStatic）自动合并，\n" +
                "纯内存、不落盘、不挂任何组件。\n" +
                "预计 Draw Call：" + before + " -> " + after + "。\n" +
                "如需撤销：Ctrl+Z，或删除 RuntimeBatchCombiner_Root。", "确定");
        }

        private void Revert()
        {
            var markers = Object.FindObjectsByType<BatchCombinedResult>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (markers == null || markers.Length == 0)
            {
                var combiners = Object.FindObjectsByType<RuntimeBatchCombiner>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (combiners != null && combiners.Length > 0)
                {
                    EditorUtility.DisplayDialog("撤销合批",
                        "场景里没有静态合批结果，但存在运行时合批器（RuntimeBatchCombiner）。\n" +
                        "运行时合批在 Awake 才执行，请在运行前用 Ctrl+Z 撤销，\n" +
                        "或手动删除 RuntimeBatchCombiner_Root。", "确定");
                }
                else
                {
                    EditorUtility.DisplayDialog("撤销合批", "场景中没有找到合批结果对象。", "确定");
                }
                return;
            }

            BatchLogger.Info("========== 开始撤销合批 ==========");

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Revert Batch");

            int revertedObjects = 0;
            int revertedRenderers = 0;

            foreach (var marker in markers)
            {
                foreach (var r in marker.sourceRenderers)
                {
                    if (r == null) continue;
                    Undo.RegisterFullObjectHierarchyUndo(r.gameObject, "Revert Batch");
                    r.enabled = true;
                    r.gameObject.SetActive(true);
                    revertedRenderers++;
                }
                Undo.DestroyObjectImmediate(marker.gameObject);
                revertedObjects++;
            }

            // 清理空掉的合批根节点
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root != null && root.name == "BatchOptimizer_Root" && root.transform.childCount == 0)
                    Undo.DestroyObjectImmediate(root);
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            BatchLogger.Info("========== 撤销合批完成：恢复 " + revertedObjects + " 个合并结果对象，共 " + revertedRenderers + " 个原始 Renderer ==========");
            _hasScan = false;
            _groups.Clear();
        }

        private void ComputeStats()
        {
            _beforeDrawCalls = 0;
            _afterDrawCalls = 0;
            _combinedMeshCount = 0;
            foreach (var g in _groups)
            {
                int chunks = MeshBatchCombiner.EstimateCombinedMeshCount(g, maxVerticesPerMesh);
                _beforeDrawCalls += g.OriginalDrawCalls;
                _afterDrawCalls += chunks * g.SubMeshCount;
                _combinedMeshCount += chunks;
            }
        }

        private bool PassesIncludeFilter(Renderer r, BatchIncludeMode mode)
        {
            bool isStatic = GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.BatchingStatic);

            bool hasTag = false;
            var tag = r.GetComponent<BatchTag>();
            if (tag != null) hasTag = tag.includeInBatching;

            switch (mode)
            {
                case BatchIncludeMode.StaticFlag: return isStatic;
                case BatchIncludeMode.BatchTag: return hasTag;
                default: return isStatic || hasTag;
            }
        }

        private string SaveMeshAsset(Mesh mesh)
        {
            const string dir = "Assets/BatchOptimizer/CombinedMeshes";
            if (!AssetDatabase.IsValidFolder(dir))
            {
                if (!AssetDatabase.IsValidFolder("Assets/BatchOptimizer"))
                    AssetDatabase.CreateFolder("Assets", "BatchOptimizer");
                AssetDatabase.CreateFolder("Assets/BatchOptimizer", "CombinedMeshes");
            }

            string scene = SceneManager.GetActiveScene().name;
            if (string.IsNullOrEmpty(scene)) scene = "Untitled";

            string path = AssetDatabase.GenerateUniqueAssetPath(dir + "/" + scene + "_" + mesh.name + ".asset");
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();

            BatchLogger.Info("已保存合并网格资源：" + path);
            return path;
        }
    }
}
