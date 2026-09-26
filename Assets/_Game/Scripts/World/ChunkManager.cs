using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace MCR
{
    /// <summary>
    /// Streams chunks around the player through the pipeline
    /// Terrain(R+3) -> Decorate(R+2, needs 3x3 terrain) -> Final -> Light(R+1, needs 3x3 final) -> Mesh(R, needs 3x3 lit).
    /// </summary>
    public sealed class ChunkManager
    {
        public readonly World world;
        readonly JobSystem jobs;
        public int renderDistance = 10;
        readonly ConcurrentQueue<Action> urgentResults = new ConcurrentQueue<Action>();
        readonly ConcurrentQueue<Action> results = new ConcurrentQueue<Action>();
        readonly HashSet<long> terrainQ = new HashSet<long>(), decorQ = new HashSet<long>(), lightQ = new HashSet<long>();
        readonly Dictionary<long, ChunkRender> renders = new Dictionary<long, ChunkRender>();
        List<Vector2Int> spiral = new List<Vector2Int>();
        int spiralRadius = -1;
        public Transform root;
        public Material[] materials;
        int pcx, pcz;
        public int loadedCount => world.chunks.Count;
        public int meshedCount;
        public int jobsInFlight => jobs.InFlight;
        public bool smoothLighting = true, fancyLeaves = true;
        public float lastUploadMs;

        public sealed class ChunkRender
        {
            public Chunk chunk;
            public GameObject[] groups;
            public Mesh[] meshes;
            public int[] version;
            public bool[] queued;
            public bool anyBuilt;
        }

        public ChunkManager(World w, JobSystem jobs, Material[] mats, Transform parent)
        {
            world = w; this.jobs = jobs; materials = mats;
            root = new GameObject("Chunks_" + w.dim).transform;
            root.SetParent(parent, false);
        }

        void EnsureSpiral(int r)
        {
            if (r == spiralRadius) return;
            spiralRadius = r;
            spiral.Clear();
            for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) spiral.Add(new Vector2Int(dx, dz));
            spiral.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
        }

        static int Cheb(int ax, int az, int bx, int bz) => Math.Max(Math.Abs(ax - bx), Math.Abs(az - bz));

        Chunk[] Neighbours(int cx, int cz, int minStage, bool needLit = false)
        {
            var nb = new Chunk[9];
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = world.GetChunk(cx + dx, cz + dz);
                    if (c == null || c.stage < minStage || (needLit && !c.lit)) return null;
                    nb[(dz + 1) * 3 + dx + 1] = c;
                }
            return nb;
        }

        public void Update(Vector3 playerPos, bool loadingScreen = false)
        {
            pcx = Mathf.FloorToInt(playerPos.x) >> 4; pcz = Mathf.FloorToInt(playerPos.z) >> 4;
            int R = renderDistance;
            EnsureSpiral(R + 3);
            // ---- apply urgent finished work first
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int applied = 0;
            while ((applied < 96 || loadingScreen) &&
                sw.ElapsedMilliseconds < (loadingScreen ? 30 : 6) &&
                urgentResults.TryDequeue(out var a))
            {
                try { a(); } catch (Exception e) { Debug.LogError("[ChunkManager] " + e); }
                applied++;
            }
            // ---- schedule
            int maxInFlight = jobs.ThreadCount * 3;
            int urgentInFlightLimit = jobs.ThreadCount * 6;
            int created = 0;
            foreach (var o in spiral)
            {
                int cx = pcx + o.x, cz = pcz + o.y;
                int d = Math.Max(Math.Abs(o.x), Math.Abs(o.y));

                // Let the near dependency ring jump ahead even when distant work
                // has already filled the normal worker budget.
                bool urgentZone = d <= 4;
                int inFlight = jobs.InFlight;
                if (inFlight >= maxInFlight &&
                    (!urgentZone || inFlight >= urgentInFlightLimit))
                    continue;

                long key = World.ChunkKey(cx, cz);
                var c = world.GetChunk(cx, cz);
                if (c == null)
                {
                    if (created >= (loadingScreen ? 64 : 12)) continue;
                    c = new Chunk(world, cx, cz);
                    world.session?.save?.PrepareChunk(world, c);
                    world.chunks[key] = c;
                    created++;

                    // Fast dependency pyramid for the first visible 3x3 chunks:
                    // terrain 9x9 -> decorate 7x7 -> light 5x5 -> mesh 3x3.
                    QueueTerrain(c, d <= 4);
                    continue;
                    }
                    if (c.stage == (int)ChunkStage.Terrain && d <= R + 2 && !decorQ.Contains(key))
                    {
                        var nb = Neighbours(cx, cz, (int)ChunkStage.Terrain);
                        if (nb != null) QueueDecorate(c, nb, d <= 3);
                    }
                    else if (c.stage == (int)ChunkStage.Final && !c.lit && d <= R + 1 && !lightQ.Contains(key))
                    {
                        var nb = Neighbours(cx, cz, (int)ChunkStage.Final);
                        if (nb != null) QueueLight(c, nb, d <= 2);
                    }
                    else if (c.lit && d <= R)
                    {
                        if (!renders.TryGetValue(key, out var cr) || !cr.anyBuilt || c.anyDirty)
                        {
                            var nb = Neighbours(cx, cz, (int)ChunkStage.Final, true);
                            if (nb != null)
                                QueueMeshes(
                                    c,
                                    nb,
                                    d <= 1 && !loadingScreen && cr != null && cr.anyBuilt,
                                    d <= 1);
                        }
                    }
            }

            // The critical path has already been scheduled above.
            // Now spend only the remaining frame budget on distant/background work.
            int resultBudget = loadingScreen ? 40 : 8;
            while ((applied < 128 || loadingScreen) &&
                sw.ElapsedMilliseconds < resultBudget &&
                results.TryDequeue(out var background))
            {
                try { background(); }
                catch (Exception e) { Debug.LogError("[ChunkManager] " + e); }
                applied++;
            }

            lastUploadMs = (float)sw.Elapsed.TotalMilliseconds;

            // ---- unload far chunks
            if (Time.frameCount % 20 == 0) UnloadFar(R + 5);
        }

        void QueueTerrain(Chunk c, bool highPriority)
        {
            long key = c.Key;
            terrainQ.Add(key);
            Interlocked.Increment(ref c.busy);
            var gen = world.generator;
            jobs.Enqueue(() =>
            {
                try { gen.GenerateTerrain(c); }
                catch (Exception e) { Debug.LogError("Terrain gen failed " + c.cx + "," + c.cz + ": " + e); }
                (highPriority ? urgentResults : results).Enqueue(() =>
                {
                    terrainQ.Remove(key);
                    Interlocked.Decrement(ref c.busy);
                    if (world.GetChunk(c.cx, c.cz) != c) return;
                    c.stage = (int)ChunkStage.Terrain;
                });
            }, highPriority);
        }

        void QueueDecorate(Chunk c, Chunk[] nb, bool highPriority)
        {
            long key = c.Key;
            decorQ.Add(key);
            Interlocked.Increment(ref c.busy);
            var gen = world.generator;
            jobs.Enqueue(() =>
            {
                try { gen.Decorate(c, nb); }
                catch (Exception e) { Debug.LogError("Decorate failed " + c.cx + "," + c.cz + ": " + e); }
                (highPriority ? urgentResults : results).Enqueue(() =>
                {
                    decorQ.Remove(key);
                    Interlocked.Decrement(ref c.busy);
                    if (world.GetChunk(c.cx, c.cz) != c) return;
                    c.decorated = true;
                    world.session?.save?.ApplyLoadedData(world, c);
                    c.RecomputeHeights();
                    c.stage = (int)ChunkStage.Final;
                });
            }, highPriority);
        }

        void QueueLight(Chunk c, Chunk[] nb, bool highPriority)
        {
            long key = c.Key;
            lightQ.Add(key);
            Interlocked.Increment(ref c.busy);

            jobs.Enqueue(() =>
            {
                try { Lighting.LightChunk(world, c, nb); }
                catch (Exception e)
                {
                    Debug.LogError("Light failed " + c.cx + "," + c.cz + ": " + e);
                }

                (highPriority ? urgentResults : results).Enqueue(() =>
                {
                    lightQ.Remove(key);
                    Interlocked.Decrement(ref c.busy);

                    if (world.GetChunk(c.cx, c.cz) != c) return;

                    c.lit = true;
                    c.stage = (int)ChunkStage.Lit;
                    c.MarkAllDirty();

                    // Mob population is not required for rendering this chunk.
                    // Keep it off the critical terrain -> mesh path, especially at night.
                    results.Enqueue(() =>
                    {
                        if (world.GetChunk(c.cx, c.cz) == c)
                            world.OnChunkLit(c);
                    });
                });
            }, highPriority);
        }

        ChunkRender GetRender(Chunk c)
        {
            long key = c.Key;
            if (!renders.TryGetValue(key, out var cr) || cr.chunk != c)
            {
                if (cr != null) DestroyRender(cr);
                int g = ChunkMesher.GroupCount(world);
                cr = new ChunkRender { chunk = c, groups = new GameObject[g], meshes = new Mesh[g], version = new int[g], queued = new bool[g] };
                renders[key] = cr;
            }
            return cr;
        }

        void QueueMeshes(Chunk c, Chunk[] nb, bool syncNear, bool highPriority)
        {
            var cr = GetRender(c);
            int groups = ChunkMesher.GroupCount(world);
            bool first = !cr.anyBuilt && !c.meshedOnce;
            for (int g = 0; g < groups; g++)
            {
                bool dirty = first;
                for (int s = g * ChunkMesher.GroupSections; s < Math.Min(c.sectionCount, (g + 1) * ChunkMesher.GroupSections); s++)
                    if (c.sectionDirty[s]) { dirty = true; c.sectionDirty[s] = false; }
                if (!dirty) continue;
                int ver = ++cr.version[g];
                int group = g;
                if (syncNear)
                {
                    var data = ChunkMesher.BuildColumn(world, nb, group, smoothLighting, fancyLeaves);
                    data.version = ver;
                    Upload(cr, data);
                }
                else
                {
                    Interlocked.Increment(ref c.busy);
                    bool smooth = smoothLighting, fancy = fancyLeaves;
                    jobs.Enqueue(() =>
                    {
                        ColumnMeshData data = null;
                        try { data = ChunkMesher.BuildColumn(world, nb, group, smooth, fancy); data.version = ver; }
                        catch (Exception e) { Debug.LogError("Mesh failed " + c.cx + "," + c.cz + ": " + e); }
                        (highPriority ? urgentResults : results).Enqueue(() =>
                        {
                            Interlocked.Decrement(ref c.busy);
                            if (data != null) Upload(cr, data);
                        });
                    }, highPriority);
                }
            }
            c.anyDirty = false;
            c.meshedOnce = true;
        }

        static readonly VertexAttributeDescriptor[] layout = ChunkVertex.Layout;

        void Upload(ChunkRender cr, ColumnMeshData d)
        {
            var c = cr.chunk;
            if (world.GetChunk(c.cx, c.cz) != c) return;
            if (!renders.TryGetValue(c.Key, out var cur) || cur != cr) return;
            if (d.version != cr.version[d.group]) return;
            int g = d.group;
            if (d.empty)
            {
                if (cr.groups[g] != null) cr.groups[g].SetActive(false);
                cr.anyBuilt = true;
                return;
            }
            var go = cr.groups[g];
            Mesh mesh = cr.meshes[g];
            if (go == null)
            {
                go = new GameObject("c" + c.cx + "_" + c.cz + "_" + g);
                go.transform.SetParent(root, false);
                go.transform.position = new Vector3(c.cx << 4, 0, c.cz << 4);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = materials;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mesh = new Mesh { name = go.name };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                cr.groups[g] = go; cr.meshes[g] = mesh;
            }
            go.SetActive(true);
            mesh.Clear(false);
            mesh.SetVertexBufferParams(d.vcount, layout);
            mesh.SetVertexBufferData(d.verts, 0, 0, d.vcount, 0, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers);
            mesh.SetIndexBufferParams(d.icount, IndexFormat.UInt32);
            mesh.SetIndexBufferData(d.indices, 0, 0, d.icount, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers);
            mesh.subMeshCount = 3;
            var flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
            for (int l = 0; l < 3; l++) mesh.SetSubMesh(l, new SubMeshDescriptor(d.layerStart[l], d.layerCount[l]), flags);
            float y0 = d.minY, y1 = d.maxY;
            mesh.bounds = new Bounds(new Vector3(8, (y0 + y1) * 0.5f, 8), new Vector3(16.5f, (y1 - y0) + 1, 16.5f));
            cr.anyBuilt = true;
        }

        void UnloadFar(int maxDist)
        {
            List<long> remove = null;
            foreach (var kv in world.chunks)
            {
                var c = kv.Value;
                if (Cheb(c.cx, c.cz, pcx, pcz) <= maxDist) continue;
                if (Volatile.Read(ref c.busy) > 0) continue;
                // don't unload if a neighbour is busy (its job might read us) — they just read, safe
                (remove ?? (remove = new List<long>())).Add(kv.Key);
            }
            if (remove == null) return;
            foreach (var k in remove)
            {
                if (!world.chunks.TryGetValue(k, out var c)) continue;
                world.OnChunkUnload(c);
                world.session?.save?.StoreChunk(world, c);
                world.chunks.TryRemove(k, out _);
                if (renders.TryGetValue(k, out var cr)) { DestroyRender(cr); renders.Remove(k); }
            }
            world.InvalidateCache();
        }

        void DestroyRender(ChunkRender cr)
        {
            for (int g = 0; g < cr.groups.Length; g++)
            {
                if (cr.meshes[g] != null) UnityEngine.Object.Destroy(cr.meshes[g]);
                if (cr.groups[g] != null) UnityEngine.Object.Destroy(cr.groups[g]);
            }
        }

        public void DestroyAll()
        {
            foreach (var cr in renders.Values) DestroyRender(cr);
            renders.Clear();
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }

        /// <summary>Force a full remesh of all loaded chunks (settings change / F3+A).</summary>
        public void ReloadAll()
        {
            foreach (var c in world.chunks.Values) if (c.lit) c.MarkAllDirty();
        }

        public bool IsAreaReady(Vector3 pos, int radius)
        {
            int cx = Mathf.FloorToInt(pos.x) >> 4, cz = Mathf.FloorToInt(pos.z) >> 4;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    var c = world.GetChunk(cx + dx, cz + dz);
                    if (c == null || !c.lit) return false;
                    if (!renders.TryGetValue(c.Key, out var cr) || !cr.anyBuilt) return false;
                }
            return true;
        }

        public float LoadProgress(Vector3 pos, int radius)
        {
            int cx = Mathf.FloorToInt(pos.x) >> 4, cz = Mathf.FloorToInt(pos.z) >> 4;
            int total = 0, done = 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    total += 4;
                    var c = world.GetChunk(cx + dx, cz + dz);
                    if (c == null) continue;
                    if (c.stage >= (int)ChunkStage.Terrain) done++;
                    if (c.stage >= (int)ChunkStage.Final) done++;
                    if (c.lit) done++;
                    if (renders.TryGetValue(c.Key, out var cr) && cr.anyBuilt) done++;
                }
            return total == 0 ? 1 : (float)done / total;
        }

        public int RenderCount => renders.Count;
    }
}
