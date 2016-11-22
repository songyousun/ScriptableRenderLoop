using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using System;

namespace UnityEngine.Experimental.ScriptableRenderLoop
{
    namespace TilePass
    {
        //-----------------------------------------------------------------------------
        // structure definition
        //-----------------------------------------------------------------------------

        [GenerateHLSL]
        public class LightDefinitions
        {
            public static int MAX_NR_LIGHTS_PER_CAMERA = 1024;
            public static int MAX_NR_BIGTILE_LIGHTS_PLUSONE = 512;      // may be overkill but the footprint is 2 bits per pixel using uint16.
            public static float VIEWPORT_SCALE_Z = 1.0f;

            // enable unity's original left-hand shader camera space (right-hand internally in unity).
            public static int USE_LEFTHAND_CAMERASPACE = 0;

            // flags
            public static int IS_CIRCULAR_SPOT_SHAPE = 1;
            public static int HAS_COOKIE_TEXTURE = 2;
            public static int IS_BOX_PROJECTED = 4;
            public static int HAS_SHADOW = 8;


            // types
            public static int MAX_TYPES = 3;

            public static int SPOT_LIGHT = 0;
            public static int SPHERE_LIGHT = 1;
            public static int BOX_LIGHT = 2;
            public static int DIRECTIONAL_LIGHT = 3;

            // direct lights and reflection probes for now
            public static int NR_LIGHT_MODELS = 2;
            public static int DIRECT_LIGHT = 0;
            public static int REFLECTION_LIGHT = 1;
        }

        [GenerateHLSL]
        public struct SFiniteLightBound
        {
            public Vector3 boxAxisX;
            public Vector3 boxAxisY;
            public Vector3 boxAxisZ;
            public Vector3 center;        // a center in camera space inside the bounding volume of the light source.
            public Vector2 scaleXY;
            public float radius;
        };

        [GenerateHLSL]
        public struct SFiniteLightData
        {
            public Vector3 lightPos;

            public Vector3 lightAxisX;
            public uint lightType;

            public Vector3 lightAxisY;
            public float radiusSq;

            public Vector3 lightAxisZ;      // spot +Z axis
            public float cotan;
 
            public Vector3 boxInnerDist;
            public uint lightModel;        // DIRECT_LIGHT=0, REFLECTION_LIGHT=1

            public Vector3 boxInvRange;
            public float unused2;
        };

        public class LightLoop
        {
            string GetKeyword()
            {
                return "LIGHTLOOP_SINGLE_PASS";
            }

            public const int MaxNumLights = 1024;
            public const int MaxNumDirLights = 2;
            public const float FltMax = 3.402823466e+38F;

            static ComputeShader buildScreenAABBShader;
            static ComputeShader buildPerTileLightListShader;     // FPTL
            static ComputeShader buildPerBigTileLightListShader;
            static ComputeShader buildPerVoxelLightListShader;    // clustered

            private static int s_GenAABBKernel;
            private static int s_GenListPerTileKernel;
            private static int s_GenListPerVoxelKernel;
            private static int s_ClearVoxelAtomicKernel;
            private static ComputeBuffer s_LightDataBuffer;
            private static ComputeBuffer s_ConvexBoundsBuffer;
            private static ComputeBuffer s_AABBBoundsBuffer;
            private static ComputeBuffer s_LightList;

            private static ComputeBuffer s_BigTileLightList;        // used for pre-pass coarse culling on 64x64 tiles
            private static int s_GenListPerBigTileKernel;

            // clustered light list specific buffers and data begin
            public bool enableClustered = false;
            public bool disableFptlWhenClustered = false;    // still useful on opaques
            public bool enableBigTilePrepass = false; // SebL - TODO: I get a crash when enabling this
            public bool enableDrawLightBoundsDebug = false;
            public bool enableDrawTileDebug = false;
            public bool enableComputeLightEvaluation = false;
            const bool k_UseDepthBuffer = true;      // only has an impact when EnableClustered is true (requires a depth-prepass)
            const bool k_UseAsyncCompute = true;        // should not use on mobile

            const int k_Log2NumClusters = 6;     // accepted range is from 0 to 6. NumClusters is 1<<g_iLog2NumClusters
            const float k_ClustLogBase = 1.02f;     // each slice 2% bigger than the previous
            float m_ClustScale;
            private static ComputeBuffer s_PerVoxelLightLists;
            private static ComputeBuffer s_PerVoxelOffset;
            private static ComputeBuffer s_PerTileLogBaseTweak;
            private static ComputeBuffer s_GlobalLightListAtomic;
            // clustered light list specific buffers and data end

            const int k_TileSize = 16;

            SFiniteLightBound[] m_boundData;
            SFiniteLightData[] m_lightData;
            int m_lightCount;

            bool usingFptl
            {
                get
                {
                    bool isEnabledMSAA = false;
                    Debug.Assert(!isEnabledMSAA || enableClustered);
                    bool disableFptl = (disableFptlWhenClustered && enableClustered) || isEnabledMSAA;
                    return !disableFptl;
                }
            }

            // Local function
            void ClearComputeBuffers()
            {
                ReleaseResolutionDependentBuffers();

                if (s_AABBBoundsBuffer != null)
                    s_AABBBoundsBuffer.Release();

                if (s_ConvexBoundsBuffer != null)
                    s_ConvexBoundsBuffer.Release();

                if (s_LightDataBuffer != null)
                    s_LightDataBuffer.Release();

                if (enableClustered)
                {
                    if (s_GlobalLightListAtomic != null)
                        s_GlobalLightListAtomic.Release();
                }
            }

            public void Rebuild()
            {
                ClearComputeBuffers();

                buildScreenAABBShader = Resources.Load<ComputeShader>("scrbound");
                buildPerTileLightListShader = Resources.Load<ComputeShader>("lightlistbuild");
                buildPerBigTileLightListShader = Resources.Load<ComputeShader>("lightlistbuild-bigtile");
                buildPerVoxelLightListShader = Resources.Load<ComputeShader>("lightlistbuild-clustered");

                s_GenAABBKernel = buildScreenAABBShader.FindKernel("ScreenBoundsAABB");
                s_GenListPerTileKernel = buildPerTileLightListShader.FindKernel(enableBigTilePrepass ? "TileLightListGen_SrcBigTile" : "TileLightListGen");
                s_AABBBoundsBuffer = new ComputeBuffer(2 * MaxNumLights, 3 * sizeof(float));
                s_ConvexBoundsBuffer = new ComputeBuffer(MaxNumLights, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SFiniteLightBound)));
                s_LightDataBuffer = new ComputeBuffer(MaxNumLights, System.Runtime.InteropServices.Marshal.SizeOf(typeof(SFiniteLightData)));
 
                buildScreenAABBShader.SetBuffer(s_GenAABBKernel, "g_data", s_ConvexBoundsBuffer);
                buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_vLightData", s_LightDataBuffer);
                buildPerTileLightListShader.SetBuffer(s_GenListPerTileKernel, "g_data", s_ConvexBoundsBuffer);

                if (enableClustered)
                {
                    var kernelName = enableBigTilePrepass ? (k_UseDepthBuffer ? "TileLightListGen_DepthRT_SrcBigTile" : "TileLightListGen_NoDepthRT_SrcBigTile") : (k_UseDepthBuffer ? "TileLightListGen_DepthRT" : "TileLightListGen_NoDepthRT");
                    s_GenListPerVoxelKernel = buildPerVoxelLightListShader.FindKernel(kernelName);
                    s_ClearVoxelAtomicKernel = buildPerVoxelLightListShader.FindKernel("ClearAtomic");
                    buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_vLightData", s_LightDataBuffer);
                    buildPerVoxelLightListShader.SetBuffer(s_GenListPerVoxelKernel, "g_data", s_ConvexBoundsBuffer);

                    s_GlobalLightListAtomic = new ComputeBuffer(1, sizeof(uint));
                }


                if (enableBigTilePrepass)
                {
                    s_GenListPerBigTileKernel = buildPerBigTileLightListShader.FindKernel("BigTileLightListGen");
                    buildPerBigTileLightListShader.SetBuffer(s_GenListPerBigTileKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    buildPerBigTileLightListShader.SetBuffer(s_GenListPerBigTileKernel, "g_vLightData", s_LightDataBuffer);
                    buildPerBigTileLightListShader.SetBuffer(s_GenListPerBigTileKernel, "g_data", s_ConvexBoundsBuffer);
                }

                m_boundData = new SFiniteLightBound[MaxNumLights];
                m_lightData = new SFiniteLightData[MaxNumLights];
                m_lightCount = 0;
            }

            public void OnDisable()
            {
                // TODO: do something for Resources.Load<ComputeShader> ?

                s_AABBBoundsBuffer.Release();
                s_ConvexBoundsBuffer.Release();
                s_LightDataBuffer.Release();
                ReleaseResolutionDependentBuffers();

                if (enableClustered)
                {
                    s_GlobalLightListAtomic.Release();
                }
            }

            
            public bool NeedResize()
            {
                return s_LightList == null || (s_BigTileLightList == null && enableBigTilePrepass) || (s_PerVoxelLightLists == null && enableClustered);
            }

            public void ReleaseResolutionDependentBuffers()
            {
                if (s_LightList != null)
                    s_LightList.Release();

                if (enableClustered)
                {
                    if (s_PerVoxelLightLists != null)
                        s_PerVoxelLightLists.Release();

                    if (s_PerVoxelOffset != null)
                        s_PerVoxelOffset.Release();

                    if (k_UseDepthBuffer && s_PerTileLogBaseTweak != null)
                        s_PerTileLogBaseTweak.Release();
                }

                if (enableBigTilePrepass)
                {
                    if (s_BigTileLightList != null)
                        s_BigTileLightList.Release();
                }
            }

            int NumLightIndicesPerClusteredTile()
            {
                return 8 * (1 << k_Log2NumClusters);       // total footprint for all layers of the tile (measured in light index entries)
            }

            public void AllocResolutionDependentBuffers(int width, int height)
            {
                var nrTilesX = (width + k_TileSize - 1) / k_TileSize;
                var nrTilesY = (height + k_TileSize - 1) / k_TileSize;
                var nrTiles = nrTilesX * nrTilesY;
                const int capacityUShortsPerTile = 32;
                const int dwordsPerTile = (capacityUShortsPerTile + 1) >> 1;        // room for 31 lights and a nrLights value.

                s_LightList = new ComputeBuffer(LightDefinitions.NR_LIGHT_MODELS * dwordsPerTile * nrTiles, sizeof(uint));       // enough list memory for a 4k x 4k display

                if (enableClustered)
                {
                    s_PerVoxelOffset = new ComputeBuffer(LightDefinitions.NR_LIGHT_MODELS * (1 << k_Log2NumClusters) * nrTiles, sizeof(uint));
                    s_PerVoxelLightLists = new ComputeBuffer(NumLightIndicesPerClusteredTile() * nrTiles, sizeof(uint));

                    if (k_UseDepthBuffer)
                    {
                        s_PerTileLogBaseTweak = new ComputeBuffer(nrTiles, sizeof(float));
                    }
                }

                if (enableBigTilePrepass)
                {
                    var nrBigTilesX = (width + 63) / 64;
                    var nrBigTilesY = (height + 63) / 64;
                    var nrBigTiles = nrBigTilesX * nrBigTilesY;
                    s_BigTileLightList = new ComputeBuffer(LightDefinitions.MAX_NR_BIGTILE_LIGHTS_PLUSONE * nrBigTiles, sizeof(uint));
                }
            }

            // TEMP: These functions should be implemented C++ side, for now do it in C#
            private static void SetMatrixCS(CommandBuffer cmd, ComputeShader shadercs, string name, Matrix4x4 mat)
            {
                var data = new float[16];

                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 4; r++)
                        data[4 * c + r] = mat[r, c];

                cmd.SetComputeFloatParams(shadercs, name, data);
            }

            private static void SetMatrixArrayCS(CommandBuffer cmd, ComputeShader shadercs, string name, Matrix4x4[] matArray)
            {
                int numMatrices = matArray.Length;
                var data = new float[numMatrices * 16];

                for (int n = 0; n < numMatrices; n++)
                    for (int c = 0; c < 4; c++)
                        for (int r = 0; r < 4; r++)
                            data[16 * n + 4 * c + r] = matArray[n][r, c];

                cmd.SetComputeFloatParams(shadercs, name, data);
            }

            private static void SetVectorArrayCS(CommandBuffer cmd, ComputeShader shadercs, string name, Vector4[] vecArray)
            {
                int numVectors = vecArray.Length;
                var data = new float[numVectors * 4];

                for (int n = 0; n < numVectors; n++)
                    for (int i = 0; i < 4; i++)
                        data[4 * n + i] = vecArray[n][i];

                cmd.SetComputeFloatParams(shadercs, name, data);
            }

            static Matrix4x4 GetFlipMatrix()
            {
                Matrix4x4 flip = Matrix4x4.identity;
                bool isLeftHand = ((int)LightDefinitions.USE_LEFTHAND_CAMERASPACE) != 0;
                if (isLeftHand) flip.SetColumn(2, new Vector4(0.0f, 0.0f, -1.0f, 0.0f));
                return flip;
            }

            static Matrix4x4 WorldToCamera(Camera camera)
            {
                return GetFlipMatrix() * camera.worldToCameraMatrix;
            }

            static Matrix4x4 CameraProjection(Camera camera)
            {
                return camera.projectionMatrix * GetFlipMatrix();
            }

            public void PrepareLightsForGPU(CullResults cullResults, Camera camera, HDRenderLoop.LightList lightList)
            {
                var numModels = (int)LightDefinitions.NR_LIGHT_MODELS;
                var numVolTypes = (int)LightDefinitions.MAX_TYPES;
                // Use for first space screen AABB
                var numEntries = new int[numModels, numVolTypes];
                var offsets = new int[numModels, numVolTypes];
                // Use for the second pass (fine pruning)
                var numEntries2nd = new int[numModels, numVolTypes];

                // TODO manage area lights
                foreach (var punctualLight in lightList.punctualLights)
                {
                    var volType = punctualLight.lightType == GPULightType.Spot ? LightDefinitions.SPOT_LIGHT : (punctualLight.lightType == GPULightType.Point ? LightDefinitions.SPHERE_LIGHT : -1);
                    if (volType >= 0)
                        ++numEntries[LightDefinitions.DIRECT_LIGHT, volType];
                }

                // TODO: manage sphere_light
                foreach (var envLight in lightList.envLights)
                {
                    var volType = LightDefinitions.BOX_LIGHT;       // always a box for now
                    ++numEntries[LightDefinitions.REFLECTION_LIGHT, volType];
                }

                // add decals here too similar to the above

                // establish offsets
                for (var m = 0; m < numModels; m++)
                {
                    offsets[m, 0] = m == 0 ? 0 : (numEntries[m - 1, numVolTypes - 1] + offsets[m - 1, numVolTypes - 1]);
                    for (var v = 1; v < numVolTypes; v++)
                    {
                        offsets[m, v] = numEntries[m, v - 1] + offsets[m, v - 1];
                    }
                }

                var worldToView = WorldToCamera(camera);

                for (int lightIndex = 0; lightIndex < lightList.punctualLights.Count; lightIndex++)
                {
                    LightData punctualLightData = lightList.punctualLights[lightIndex];
                    VisibleLight light = cullResults.visibleLights[lightList.punctualCullIndices[lightIndex]];

                    var range = light.range;
                    var lightToWorld = light.localToWorld;
                    Vector3 lightPos = lightToWorld.GetColumn(3);

                    // Fill bounds
                    var bound = new SFiniteLightBound();
                    var lightData = new SFiniteLightData();
                    int index = -1;

                    lightData.lightModel = (uint)LightDefinitions.DIRECT_LIGHT;

                    if (punctualLightData.lightType == GPULightType.Spot || punctualLightData.lightType == GPULightType.ProjectorPyramid)
                    {
                        Vector3 lightDir = lightToWorld.GetColumn(2);   // Z axis in world space

                        // represents a left hand coordinate system in world space
                        Vector3 vx = lightToWorld.GetColumn(0);     // X axis in world space
                        Vector3 vy = lightToWorld.GetColumn(1);     // Y axis in world space
                        var vz = lightDir;                      // Z axis in world space

                        // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                        vx = worldToView.MultiplyVector(vx);
                        vy = worldToView.MultiplyVector(vy);
                        vz = worldToView.MultiplyVector(vz);


                        const float pi = 3.1415926535897932384626433832795f;
                        const float degToRad = (float)(pi / 180.0);


                        var sa = light.light.spotAngle;

                        var cs = Mathf.Cos(0.5f * sa * degToRad);
                        var si = Mathf.Sin(0.5f * sa * degToRad);
                        var ta = cs > 0.0f ? (si / cs) : FltMax;

                        var cota = si > 0.0f ? (cs / si) : FltMax;

                        //const float cotasa = l.GetCotanHalfSpotAngle();

                        // apply nonuniform scale to OBB of spot light
                        var squeeze = true;//sa < 0.7f * 90.0f;      // arb heuristic
                        var fS = squeeze ? ta : si;
                        bound.center = worldToView.MultiplyPoint(lightPos + ((0.5f * range) * lightDir));    // use mid point of the spot as the center of the bounding volume for building screen-space AABB for tiled lighting.

                        // scale axis to match box or base of pyramid
                        bound.boxAxisX = (fS * range) * vx;
                        bound.boxAxisY = (fS * range) * vy;
                        bound.boxAxisZ = (0.5f * range) * vz;

                        // generate bounding sphere radius
                        var fAltDx = si;
                        var fAltDy = cs;
                        fAltDy = fAltDy - 0.5f;
                        //if(fAltDy<0) fAltDy=-fAltDy;

                        fAltDx *= range; fAltDy *= range;

                        var altDist = Mathf.Sqrt(fAltDy * fAltDy + (punctualLightData.lightType == GPULightType.Spot ? 1.0f : 2.0f) * fAltDx * fAltDx);
                        bound.radius = altDist > (0.5f * range) ? altDist : (0.5f * range);       // will always pick fAltDist
                        bound.scaleXY = squeeze ? new Vector2(0.01f, 0.01f) : new Vector2(1.0f, 1.0f);


                        lightData.lightAxisX = vx;
                        lightData.lightAxisY = vy;
                        lightData.lightAxisZ = vz;
                        lightData.lightType = (uint)LightDefinitions.SPOT_LIGHT;
                        lightData.lightPos = worldToView.MultiplyPoint(lightPos);
                        lightData.radiusSq = range * range;
                        lightData.cotan = cota;

                        int i = LightDefinitions.DIRECT_LIGHT, j = LightDefinitions.SPOT_LIGHT;
                        index = numEntries2nd[i, j] + offsets[i, j]; ++numEntries2nd[i, j];
                    }
                    else // if (punctualLightData.lightType == GPULightType.Point)
                    {
                        bool isNegDeterminant = Vector3.Dot(worldToView.GetColumn(0), Vector3.Cross(worldToView.GetColumn(1), worldToView.GetColumn(2))) < 0.0f;      // 3x3 Determinant.

                        bound.center = worldToView.MultiplyPoint(lightPos);
                        bound.boxAxisX.Set(range, 0, 0);
                        bound.boxAxisY.Set(0, range, 0);
                        bound.boxAxisZ.Set(0, 0, isNegDeterminant ? (-range) : range);    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                        bound.scaleXY.Set(1.0f, 1.0f);
                        bound.radius = range;

                        // represents a left hand coordinate system in world space since det(worldToView)<0
                        var lightToView = worldToView * lightToWorld;
                        Vector3 vx = lightToView.GetColumn(0);
                        Vector3 vy = lightToView.GetColumn(1);
                        Vector3 vz = lightToView.GetColumn(2);

                        // fill up ldata
                        lightData.lightAxisX = vx;
                        lightData.lightAxisY = vy;
                        lightData.lightAxisZ = vz;
                        lightData.lightType = (uint)LightDefinitions.SPHERE_LIGHT;
                        lightData.lightPos = bound.center;
                        lightData.radiusSq = range * range;

                        int i = LightDefinitions.DIRECT_LIGHT, j = LightDefinitions.SPHERE_LIGHT;
                        index = numEntries2nd[i, j] + offsets[i, j]; ++numEntries2nd[i, j];
                    }

                    m_boundData[index] = bound;
                    m_lightData[index] = lightData;
                }

                for (int envIndex = 0; envIndex < lightList.envLights.Count; envIndex++)
                {
                    EnvLightData envLightData = lightList.envLights[envIndex];
                    VisibleReflectionProbe probe = cullResults.visibleReflectionProbes[lightList.envCullIndices[envIndex]];

                    var bound = new SFiniteLightBound();
                    var lightData = new SFiniteLightData();

                    var bnds = probe.bounds;
                    var boxOffset = probe.center;                  // reflection volume offset relative to cube map capture point
                    var blendDistance = probe.blendDistance;

                    var mat = probe.localToWorld;

                    // C is reflection volume center in world space (NOT same as cube map capture point)
                    var e = bnds.extents;       // 0.5f * Vector3.Max(-boxSizes[p], boxSizes[p]);
                    //Vector3 C = bnds.center;        // P + boxOffset;
                    var C = mat.MultiplyPoint(boxOffset);       // same as commented out line above when rot is identity

                   var combinedExtent = e + new Vector3(blendDistance, blendDistance, blendDistance);

                    Vector3 vx = mat.GetColumn(0);
                    Vector3 vy = mat.GetColumn(1);
                    Vector3 vz = mat.GetColumn(2);

                    // transform to camera space (becomes a left hand coordinate frame in Unity since Determinant(worldToView)<0)
                    vx = worldToView.MultiplyVector(vx);
                    vy = worldToView.MultiplyVector(vy);
                    vz = worldToView.MultiplyVector(vz);

                    var Cw = worldToView.MultiplyPoint(C);

                    bound.center = Cw;
                    bound.boxAxisX = combinedExtent.x * vx;
                    bound.boxAxisY = combinedExtent.y * vy;
                    bound.boxAxisZ = combinedExtent.z * vz;
                    bound.scaleXY.Set(1.0f, 1.0f);
                    bound.radius = combinedExtent.magnitude;

                    lightData.lightPos = Cw;
                    lightData.lightAxisX = vx;
                    lightData.lightAxisY = vy;
                    lightData.lightAxisZ = vz;
                    var delta = combinedExtent - e;
                    lightData.boxInnerDist = e;
                    lightData.boxInvRange.Set(1.0f / delta.x, 1.0f / delta.y, 1.0f / delta.z);

                    int i = LightDefinitions.REFLECTION_LIGHT, j = LightDefinitions.BOX_LIGHT;
                    int index = numEntries2nd[i, j] + offsets[i, j]; ++numEntries2nd[i, j];
                    m_boundData[index] = bound;
                }

                // Sanity check
                for (var m = 0; m < numModels; m++)
                {
                    for (var v = 0; v < numVolTypes; v++)
                    {
                        Debug.Assert(numEntries[m, v] == numEntries2nd[m, v], "count mismatch on second pass!");
                    }
                }

                m_lightCount = lightList.punctualLights.Count + lightList.envLights.Count;
                s_ConvexBoundsBuffer.SetData(m_boundData); // TODO: check with Vlad what is happening here, do we copy 1024 element always ? Could we setup the size we want to copy ?
            }

            void VoxelLightListGeneration(CommandBuffer cmd, Camera camera, Matrix4x4 projscr, Matrix4x4 invProjscr, int cameraDepthBuffer)
            {
                // clear atomic offset index
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);
                cmd.DispatchCompute(buildPerVoxelLightListShader, s_ClearVoxelAtomicKernel, 1, 1, 1);

                cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iNrVisibLights", m_lightCount);
                SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mScrProjection", projscr);
                SetMatrixCS(cmd, buildPerVoxelLightListShader, "g_mInvScrProjection", invProjscr);

                cmd.SetComputeIntParam(buildPerVoxelLightListShader, "g_iLog2NumClusters", k_Log2NumClusters);

                //Vector4 v2_near = invProjscr * new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
                //Vector4 v2_far = invProjscr * new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
                //float nearPlane2 = -(v2_near.z/v2_near.w);
                //float farPlane2 = -(v2_far.z/v2_far.w);
                var nearPlane = camera.nearClipPlane;
                var farPlane = camera.farClipPlane;
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fNearPlane", nearPlane);
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fFarPlane", farPlane);

                const float C = (float)(1 << k_Log2NumClusters);
                var geomSeries = (1.0 - Mathf.Pow(k_ClustLogBase, C)) / (1 - k_ClustLogBase);        // geometric series: sum_k=0^{C-1} base^k
                m_ClustScale = (float)(geomSeries / (farPlane - nearPlane));

                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustScale", m_ClustScale);
                cmd.SetComputeFloatParam(buildPerVoxelLightListShader, "g_fClustBase", k_ClustLogBase);

                cmd.SetComputeTextureParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_depth_tex", new RenderTargetIdentifier(cameraDepthBuffer));
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vLayeredLightList", s_PerVoxelLightLists);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredOffset", s_PerVoxelOffset);
                cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_LayeredSingleIdxBuffer", s_GlobalLightListAtomic);
                if (enableBigTilePrepass) cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_vBigTileLightList", s_BigTileLightList);

                if (k_UseDepthBuffer)
                {
                    cmd.SetComputeBufferParam(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, "g_logBaseBuffer", s_PerTileLogBaseTweak);
                }

                var numTilesX = (camera.pixelWidth + 15) / 16;
                var numTilesY = (camera.pixelHeight + 15) / 16;
                cmd.DispatchCompute(buildPerVoxelLightListShader, s_GenListPerVoxelKernel, numTilesX, numTilesY, 1);
            }

            public void BuildGPULightLists(Camera camera, RenderLoop loop, HDRenderLoop.LightList lightList, int cameraDepthBuffer)
            {
                var w = camera.pixelWidth;
                var h = camera.pixelHeight;
                var numTilesX = (w + 15) / 16;
                var numTilesY = (h + 15) / 16;
                var numBigTilesX = (w + 63) / 64;
                var numBigTilesY = (h + 63) / 64;

                // camera to screen matrix (and it's inverse)
                var proj = CameraProjection(camera);
                var temp = new Matrix4x4();
                temp.SetRow(0, new Vector4(0.5f * w, 0.0f, 0.0f, 0.5f * w));
                temp.SetRow(1, new Vector4(0.0f, 0.5f * h, 0.0f, 0.5f * h));
                temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
                var projscr = temp * proj;
                var invProjscr = projscr.inverse;

                var cmd = new CommandBuffer() { name = "Build light list" };

                // generate screen-space AABBs (used for both fptl and clustered).
                {
                    temp.SetRow(0, new Vector4(1.0f, 0.0f, 0.0f, 0.0f));
                    temp.SetRow(1, new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
                    temp.SetRow(2, new Vector4(0.0f, 0.0f, 0.5f, 0.5f));
                    temp.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
                    var projh = temp * proj;
                    var invProjh = projh.inverse;

                    cmd.SetComputeIntParam(buildScreenAABBShader, "g_iNrVisibLights", m_lightCount);
                    SetMatrixCS(cmd, buildScreenAABBShader, "g_mProjection", projh);
                    SetMatrixCS(cmd, buildScreenAABBShader, "g_mInvProjection", invProjh);
                    cmd.SetComputeBufferParam(buildScreenAABBShader, s_GenAABBKernel, "g_vBoundsBuffer", s_AABBBoundsBuffer);
                    cmd.DispatchCompute(buildScreenAABBShader, s_GenAABBKernel, (m_lightCount + 7) / 8, 1, 1);
                }

                // enable coarse 2D pass on 64x64 tiles (used for both fptl and clustered).
                if (enableBigTilePrepass)
                {
                    cmd.SetComputeIntParams(buildPerBigTileLightListShader, "g_viDimensions", new int[2] { w, h });
                    cmd.SetComputeIntParam(buildPerBigTileLightListShader, "g_iNrVisibLights", m_lightCount);
                    SetMatrixCS(cmd, buildPerBigTileLightListShader, "g_mScrProjection", projscr);
                    SetMatrixCS(cmd, buildPerBigTileLightListShader, "g_mInvScrProjection", invProjscr);
                    cmd.SetComputeFloatParam(buildPerBigTileLightListShader, "g_fNearPlane", camera.nearClipPlane);
                    cmd.SetComputeFloatParam(buildPerBigTileLightListShader, "g_fFarPlane", camera.farClipPlane);
                    cmd.SetComputeBufferParam(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, "g_vLightList", s_BigTileLightList);
                    cmd.DispatchCompute(buildPerBigTileLightListShader, s_GenListPerBigTileKernel, numBigTilesX, numBigTilesY, 1);
                }

                if (usingFptl)       // optimized for opaques only
                {
                    cmd.SetComputeIntParams(buildPerTileLightListShader, "g_viDimensions", new int[2] { w, h });
                    cmd.SetComputeIntParam(buildPerTileLightListShader, "g_iNrVisibLights", m_lightCount);
                    SetMatrixCS(cmd, buildPerTileLightListShader, "g_mScrProjection", projscr);
                    SetMatrixCS(cmd, buildPerTileLightListShader, "g_mInvScrProjection", invProjscr);
                    cmd.SetComputeTextureParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_depth_tex", new RenderTargetIdentifier(cameraDepthBuffer));
                    cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vLightList", s_LightList);
                    if (enableBigTilePrepass) cmd.SetComputeBufferParam(buildPerTileLightListShader, s_GenListPerTileKernel, "g_vBigTileLightList", s_BigTileLightList);
                    cmd.DispatchCompute(buildPerTileLightListShader, s_GenListPerTileKernel, numTilesX, numTilesY, 1);
                }

                if (enableClustered)        // works for transparencies too.
                {
                    VoxelLightListGeneration(cmd, camera, projscr, invProjscr, cameraDepthBuffer);
                }

                loop.ExecuteCommandBuffer(cmd);
                cmd.Dispose();
            }

            public void PushGlobalParams(Camera camera, RenderLoop loop, HDRenderLoop.LightList lightList)
            {
            }
        }
    }
}