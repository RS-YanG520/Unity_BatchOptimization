using System.Collections.Generic;
using UnityEngine;

namespace BatchOptimizer
{
    /// <summary>
    /// 挂在「合并后生成」的 GameObject 上，记录被合并进来的原始 Renderer，
    /// 供撤销（Revert）时恢复原始对象使用。
    /// </summary>
    [DisallowMultipleComponent]
    public class BatchCombinedResult : MonoBehaviour
    {
        [HideInInspector]
        public List<Renderer> sourceRenderers = new List<Renderer>();

        [HideInInspector]
        public int drawCallsBefore;

        [HideInInspector]
        public int drawCallsAfter;
    }
}
