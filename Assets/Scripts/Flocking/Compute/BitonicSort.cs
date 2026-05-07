// BitonicSort.cs — Allocation-free dispatch wrapper for BitonicSort.compute.
//
// USAGE CONTRACT (caller responsibility):
//
//   var buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, paddedLen, 8);
//   buf.SetData(randomKeyValuePairs);                    // tail = uint2(~0u, ~0u)
//   BitonicSort.Sort(shader, buf, liveElementCount);
//   AsyncGPUReadback.Request(buf, r => { /* assert r.GetData<uint2>().x ascending */ });
//
// The CALLER owns buffer creation/disposal and tail padding with sentinels —
// this keeps the wrapper allocation-free across calls. See the compute shader
// for the padding-sentinel contract.
//
// WHY STATIC AND NOT MONOBEHAVIOUR:
// FlockWorld drives sort frequency from its own scheduler; this wrapper is a
// pure function of (shader, buffer, count). A MonoBehaviour wrapper would add
// a managed object and serialisation surface for zero benefit.

using UnityEngine;

namespace Bird_behiviour.Flocking.Compute
{
    /// <summary>
    /// GPU bitonic sort over a <see cref="GraphicsBuffer"/> of <c>uint2</c>
    /// (key in <c>.x</c>, payload in <c>.y</c>). Sorts ascending, in place.
    /// </summary>
    public static class BitonicSort
    {
        // PropertyToID hashes once per domain reload, not per call. Caching them
        // here also makes it impossible for a typo to silently no-op a SetInt.
        private static readonly int s_DataId = Shader.PropertyToID("_Data");
        private static readonly int s_StageId = Shader.PropertyToID("_Stage");
        private static readonly int s_PassId = Shader.PropertyToID("_Pass");
        private static readonly int s_PaddedLengthId = Shader.PropertyToID("_PaddedLength");

        // Matches numthreads(64,1,1) in the compute shader. Hard-coded rather
        // than introspected because Unity's reflection path on ComputeShader
        // allocates a managed array per query.
        private const int ThreadGroupSize = 64;

        /// <summary>
        /// Sorts <paramref name="keyValueBuffer"/> ascending by the <c>.x</c>
        /// component of each <c>uint2</c>, in place.
        /// </summary>
        /// <param name="shader">Loaded <c>BitonicSort.compute</c> instance. Caller owns lifetime.</param>
        /// <param name="keyValueBuffer">Structured buffer of <c>uint2</c>, stride 8. Length MUST be a power of two; tail past <paramref name="elementCount"/> MUST be filled with <c>uint2(0xFFFFFFFF, 0xFFFFFFFF)</c> sentinels.</param>
        /// <param name="elementCount">Live element count. Used only to derive the padded length the shader sees; the buffer itself is treated as power-of-two long.</param>
        public static void Sort(ComputeShader shader, GraphicsBuffer keyValueBuffer, int elementCount)
        {
            if (shader == null)
            {
                throw new System.ArgumentNullException(nameof(shader));
            }
            if (keyValueBuffer == null)
            {
                throw new System.ArgumentNullException(nameof(keyValueBuffer));
            }
            if (elementCount <= 1)
            {
                // 0 or 1 elements are trivially sorted — skipping the dispatch
                // also avoids a 1<<-1 underflow when computing log2.
                return;
            }

            int paddedLength = NextPowerOfTwo(elementCount);
            if (paddedLength > keyValueBuffer.count)
            {
                throw new System.ArgumentException(
                    $"Buffer length {keyValueBuffer.count} is smaller than next-pow2 padded length {paddedLength} for {elementCount} elements.",
                    nameof(keyValueBuffer));
            }

            int stages = IntegerLog2(paddedLength);
            int kernel = 0; // single kernel in this .compute — index is stable.

            // Bind once per Sort call. _Data is a buffer binding (no per-stage
            // change) and _PaddedLength is constant for the whole sort.
            shader.SetBuffer(kernel, s_DataId, keyValueBuffer);
            shader.SetInt(s_PaddedLengthId, paddedLength);

            int groups = (paddedLength + ThreadGroupSize - 1) / ThreadGroupSize;

            // Batcher's network: K stages, stage k contains k sub-passes.
            // Total dispatches = K*(K+1)/2. The global memory write at the end
            // of each dispatch is our cross-group barrier — no GroupMemoryBarrier
            // would be sufficient because partner threads can live in any group.
            for (int stage = 1; stage <= stages; ++stage)
            {
                shader.SetInt(s_StageId, stage);
                for (int pass = 0; pass < stage; ++pass)
                {
                    shader.SetInt(s_PassId, pass);
                    shader.Dispatch(kernel, groups, 1, 1);
                }
            }
        }

        // Branchless next-power-of-two for 32-bit positive ints. Bit-trick is
        // ~5x faster than the obvious `while (x < n) x <<= 1` loop and avoids
        // a managed call to Math.Pow.
        private static int NextPowerOfTwo(int n)
        {
            if (n < 2)
            {
                return 1;
            }
            uint v = (uint)(n - 1);
            v |= v >> 1;
            v |= v >> 2;
            v |= v >> 4;
            v |= v >> 8;
            v |= v >> 16;
            return (int)(v + 1u);
        }

        // Caller has already guaranteed `n` is a positive power of two; this
        // returns the bit position of the single set bit.
        private static int IntegerLog2(int n)
        {
            int log = 0;
            while ((n >>= 1) > 0)
            {
                ++log;
            }
            return log;
        }
    }
}
