﻿/*
Ferram Aerospace Research v0.15.2 "Ferri"
=========================
Aerodynamics model for Kerbal Space Program

Copyright 2015, Michael Ferrara, aka Ferram4

   This file is part of Ferram Aerospace Research.

   Ferram Aerospace Research is free software: you can redistribute it and/or modify
   it under the terms of the GNU General Public License as published by
   the Free Software Foundation, either version 3 of the License, or
   (at your option) any later version.

   Ferram Aerospace Research is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
   GNU General Public License for more details.

   You should have received a copy of the GNU General Public License
   along with Ferram Aerospace Research.  If not, see <http://www.gnu.org/licenses/>.

   Serious thanks:		a.g., for tons of bugfixes and code-refactorings   
				stupid_chris, for the RealChuteLite implementation
            			Taverius, for correcting a ton of incorrect values  
				Tetryds, for finding lots of bugs and issues and not letting me get away with them, and work on example crafts
            			sarbian, for refactoring code for working with MechJeb, and the Module Manager updates  
            			ialdabaoth (who is awesome), who originally created Module Manager  
                        	Regex, for adding RPM support  
				DaMichel, for some ferramGraph updates and some control surface-related features  
            			Duxwing, for copy editing the readme  
   
   CompatibilityChecker by Majiir, BSD 2-clause http://opensource.org/licenses/BSD-2-Clause

   Part.cfg changes powered by sarbian & ialdabaoth's ModuleManager plugin; used with permission  
	http://forum.kerbalspaceprogram.com/threads/55219

   ModularFLightIntegrator by Sarbian, Starwaster and Ferram4, MIT: http://opensource.org/licenses/MIT
	http://forum.kerbalspaceprogram.com/threads/118088

   Toolbar integration powered by blizzy78's Toolbar plugin; used with permission  
	http://forum.kerbalspaceprogram.com/threads/60863
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;
using KSP;
using FerramAerospaceResearch.FARPartGeometry.GeometryModification;

namespace FerramAerospaceResearch.FARPartGeometry
{
    public class GeometryPartModule : PartModule, TweakScale.IRescalable<GeometryPartModule>
    {
        public Transform partTransform;
        public Rigidbody partRigidBody;

        public Bounds overallMeshBounds;

        public List<GeometryMesh> meshDataList;
        private List<IGeometryUpdater> geometryUpdaters;
        private List<ICrossSectionAdjuster> crossSectionAdjusters;

        private List<AnimationState> animStates;
        private List<float> animStateTime;

        private bool _started = false;
        private bool _ready = false;
        public bool Ready
        {
            get { return _ready && _started; }
        }
        private int _sendUpdateTick = 0;
        private int _meshesToUpdate = 0;

        private float currentScaleFactor = 1;

        [SerializeField]
        bool forceUseColliders;
        [SerializeField]
        bool forceUseMeshes;

        void Start()
        {
            //RebuildAllMeshData();
            SetupIGeometryUpdaters();
            SetupICrossSectionAdjusters();
            GetAnimations();
        }

        void OnDestroy()
        {
            meshDataList = null;
            geometryUpdaters = null;
            crossSectionAdjusters = null;
            animStates = null;
            animStateTime = null;
        }

        void FixedUpdate()
        {
            if (!_started && ((HighLogic.LoadedSceneIsFlight && FlightGlobals.ready) ||       //this is done because it takes a frame for colliders to be set up in the editor
            HighLogic.LoadedSceneIsEditor && ApplicationLauncher.Ready))                //waiting prevents changes in physics in flight or in predictions because the voxel switches to colliders rather than meshes
            {
                RebuildAllMeshData();
                _started = true;
            }
            if(!_ready && _meshesToUpdate == 0)
                _ready = true;

            if (animStates != null && animStates.Count > 0)
                CheckAnimations();
        }

        public void ClearMeshData()
        {
            meshDataList = null;
            _ready = false;
        }

        public void GeometryPartModuleRebuildMeshData()
        {
            RebuildAllMeshData();
            UpdateVoxelShape();
        }

        internal void RebuildAllMeshData()
        {
            if(!(HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor))
                return;

            partTransform = part.partTransform;
            List<Transform> meshTransforms = part.PartModelTransformList();
            List<MeshData> geometryMeshes = CreateMeshListFromTransforms(ref meshTransforms);

            meshDataList = new List<GeometryMesh>();

            Matrix4x4 worldToVesselMatrix;
            if (HighLogic.LoadedSceneIsFlight)
            {
                worldToVesselMatrix = vessel.transform.worldToLocalMatrix;
            }
            else
            {
                worldToVesselMatrix = EditorLogic.RootPart.partTransform.worldToLocalMatrix;
            }
            for (int i = 0; i < meshTransforms.Count; i++)
            {
                MeshData m = geometryMeshes[i];
                GeometryMesh geoMesh = new GeometryMesh(m.vertices, m.triangles, m.bounds, meshTransforms[i], worldToVesselMatrix, this);
                meshDataList.Add(geoMesh);
            }
            //UpdateTransformMatrixList(worldToVesselMatrix);
            overallMeshBounds = part.GetPartOverallMeshBoundsInBasis(worldToVesselMatrix);
        }

        private void GetAnimations()
        {
            Animation[] animations = part.FindModelAnimators();

            if (animations.Length == 0)
                return;

            animStates = new List<AnimationState>();
            animStateTime = new List<float>();

            foreach (PartModule m in part.Modules)
            {
                FindAnimStatesInModule(animations, m, "animationName");
                FindAnimStatesInModule(animations, m, "animName");
                FindAnimStatesInModule(animations, m, "deployAnimationName");
            }
        }

        private void FindAnimStatesInModule(Animation[] animations, PartModule m, string fieldName)
        {
            FieldInfo field = m.GetType().GetField(fieldName);
            if (field != null)        //This handles stock and Firespitter deployment animations
            {
                string animationName = (string)field.GetValue(m);
                for (int i = 0; i < animations.Length; i++)
                {
                    Animation anim = animations[i];

                    if (anim != null)
                    {
                        AnimationState state = anim[animationName];
                        if (state)
                        {
                            animStates.Add(state);
                            animStateTime.Add(state.time);
                        }
                    }
                }
            }
        }

        private void SetupIGeometryUpdaters()
        {
            geometryUpdaters = new List<IGeometryUpdater>();
            if(part is CompoundPart)
            {
                CompoundPartGeoUpdater compoundUpdate = new CompoundPartGeoUpdater((CompoundPart)part, this);
                geometryUpdaters.Add(compoundUpdate);
            }
            if(part.Modules.Contains("ModuleProceduralFairing"))
            {
                ModuleProceduralFairing[] fairings = part.GetComponents<ModuleProceduralFairing>();
                for (int i = 0; i < fairings.Length; i++)
                {
                    ModuleProceduralFairing fairing = fairings[i];

                    StockProcFairingGeoUpdater fairingUpdater = new StockProcFairingGeoUpdater(fairing, this);
                    geometryUpdaters.Add(fairingUpdater);
                }
            }
            if(part.Modules.Contains("ModuleJettison"))
            {
                ModuleJettison[] engineFairings = part.GetComponents<ModuleJettison>();
                for (int i = 0; i < engineFairings.Length; i++)
                {
                    ModuleJettison engineFairing = engineFairings[i];

                    StockJettisonTransformGeoUpdater fairingUpdater = new StockJettisonTransformGeoUpdater(engineFairing, this);
                    geometryUpdaters.Add(fairingUpdater);
                }
            }
        }

        private void SetupICrossSectionAdjusters()
        {
            Matrix4x4 worldToVesselMatrix;
            if (HighLogic.LoadedSceneIsFlight)
            {
                worldToVesselMatrix = vessel.transform.worldToLocalMatrix;
            }
            else
            {
                worldToVesselMatrix = EditorLogic.RootPart.partTransform.worldToLocalMatrix;
            } 
            crossSectionAdjusters = new List<ICrossSectionAdjuster>();
            if(part.Modules.Contains("ModuleEngines"))
            {
                ModuleEngines engines = (ModuleEngines)part.Modules["ModuleEngines"];
                for(int i = 0; i < engines.propellants.Count; i++)
                {
                    Propellant p = engines.propellants[i];
                    if (p.name == "IntakeAir")
                    {
                        AirbreathingEngineCrossSectonAdjuster engineAdjuster = new AirbreathingEngineCrossSectonAdjuster(engines, worldToVesselMatrix);
                        crossSectionAdjusters.Add(engineAdjuster);
                        break;
                    }
                }
            }
            if (part.Modules.Contains("ModuleEnginesFX"))
            {
                ModuleEnginesFX engines = (ModuleEnginesFX)part.Modules["ModuleEnginesFX"];
                for (int i = 0; i < engines.propellants.Count; i++)
                {
                    Propellant p = engines.propellants[i];
                    if (p.name == "IntakeAir")
                    {
                        AirbreathingEngineCrossSectonAdjuster engineAdjuster = new AirbreathingEngineCrossSectonAdjuster(engines, worldToVesselMatrix);
                        crossSectionAdjusters.Add(engineAdjuster);
                        break;
                    }
                }
            }
            if (part.Modules.Contains("ModuleResourceIntake"))
            {
                ModuleResourceIntake intake = (ModuleResourceIntake)part.Modules["ModuleResourceIntake"];

                IntakeCrossSectionAdjuster engineAdjuster = new IntakeCrossSectionAdjuster(intake, worldToVesselMatrix);
                crossSectionAdjusters.Add(engineAdjuster);
            }
        }

        public void RunIGeometryUpdaters()
        {
            if (HighLogic.LoadedSceneIsEditor)
                for (int i = 0; i < geometryUpdaters.Count; i++)
                    geometryUpdaters[i].EditorGeometryUpdate();
            else if (HighLogic.LoadedSceneIsFlight)
                for (int i = 0; i < geometryUpdaters.Count; i++)
                    geometryUpdaters[i].FlightGeometryUpdate();
        }

        public void GetICrossSectionAdjusters(List<ICrossSectionAdjuster> forwardFacing, List<ICrossSectionAdjuster> rearwardFacing, Matrix4x4 basis, Vector3 vehicleMainAxis)
        {
            for(int i = 0; i < crossSectionAdjusters.Count;i++)
            {
                ICrossSectionAdjuster adjuster = crossSectionAdjusters[i];
                adjuster.TransformBasis(basis);

                if (adjuster.AreaRemovedFromCrossSection(vehicleMainAxis) != 0)
                    forwardFacing.Add(adjuster);
                else if (adjuster.AreaRemovedFromCrossSection(-vehicleMainAxis) != 0)
                    rearwardFacing.Add(adjuster);
            }
        }

        #region voxelUpdates
        private void CheckAnimations()
        {
            bool updateShape = false;
            if (_sendUpdateTick > 30)
            {
                _sendUpdateTick = 0;
                for (int i = 0; i < animStates.Count; i++)
                {
                    AnimationState state = animStates[i];
                    float prevNormTime = animStateTime[i];

                    //if (state.speed != 0)     //if the animation is playing, send the event
                    //{
                    //    UpdateShapeWithAnims(); //event to update voxel, with rate limiter for computer's sanity and error reduction
                    //    break;
                    //}
                    if (Math.Abs(prevNormTime - state.time) > float.Epsilon * 2f)       //if the anim is not playing, but it was, also send the event to be sure that we closed
                    {
                        animStateTime[i] = state.time;
                        updateShape = true;
                    }
                }
            }
            else
                ++_sendUpdateTick;

            if(updateShape)
                UpdateShapeWithAnims(); //event to update voxel, with rate limiter for computer's sanity and error reduction
        }

        private void UpdateShapeWithAnims()
        {
            Matrix4x4 transformMatrix;
            if (HighLogic.LoadedSceneIsFlight)
                transformMatrix = vessel.transform.worldToLocalMatrix;
            else
                transformMatrix = EditorLogic.RootPart.partTransform.worldToLocalMatrix;

            UpdateTransformMatrixList(transformMatrix);

            UpdateVoxelShape();
        }

        private void UpdateVoxelShape()
        {
            if (HighLogic.LoadedSceneIsFlight)
                vessel.SendMessage("AnimationVoxelUpdate");
            else if (HighLogic.LoadedSceneIsEditor)
                FARGUI.FAREditorGUI.EditorGUI.RequestUpdateVoxel();
        }

        public void EditorUpdate()
        {
            Matrix4x4 transformMatrix = EditorLogic.RootPart.partTransform.worldToLocalMatrix;
            UpdateTransformMatrixList(transformMatrix);
        }

        public void UpdateTransformMatrixList(Matrix4x4 worldToVesselMatrix)
        {
            _ready = false;
            if (meshDataList != null)
            {
                _meshesToUpdate = meshDataList.Count;
                for (int i = 0; i < meshDataList.Count; ++i)
                {
                    GeometryMesh mesh = meshDataList[i];
                    if (mesh.TrySetThisToVesselMatrixForTransform())
                    {
                        ThreadPool.QueueUserWorkItem(mesh.MultithreadTransformBasis, worldToVesselMatrix);
                        //mesh.TransformBasis(worldToVesselMatrix);
                    }
                    else
                    {
                        meshDataList.RemoveAt(i);
                        --i;
                        lock (this)
                            --_meshesToUpdate;
                    }
                    /*if (!meshDataList[i].TryTransformBasis(worldToVesselMatrix))
                    {
                        meshDataList.RemoveAt(i);
                        i--;
                    }*/
                }
            }
            if(crossSectionAdjusters != null)
            {
                for(int i = 0; i < crossSectionAdjusters.Count; i++)
                {
                    crossSectionAdjusters[i].SetThisToVesselMatrixForTransform();
                }
            }
            overallMeshBounds = part.GetPartOverallMeshBoundsInBasis(worldToVesselMatrix);
        }

        internal void DecrementMeshesToUpdate()
        {
            lock (this)
                --_meshesToUpdate;
        }

        #endregion

        private MeshData GetColliderMeshData(Transform t)
        {
            MeshCollider mc = t.GetComponent<MeshCollider>();
            if (mc != null)
            {
                //Mesh m = mc.sharedMesh;       //we can't used mc.sharedMesh because it does not contain all the triangles or verts for some reason
                                                //must instead get the mesh filter and use its shared mesh

                MeshFilter mf = t.GetComponent<MeshFilter>();
                if (mf != null)
                {
                    Mesh m = mf.sharedMesh;

                    if (m != null)
                        return new MeshData(m.vertices, m.triangles, m.bounds);
                }
                else
                {
                    Mesh m = mc.sharedMesh;

                    if (m != null)
                        return new MeshData(m.vertices, m.triangles, m.bounds);
                }
            }
            else
            {
                BoxCollider bc = t.GetComponent<BoxCollider>();
                if (bc != null)
                {
                    return CreateBoxMeshFromBoxCollider(bc.size, bc.center);
                }
            }
            return null;
        }

        private MeshData GetVisibleMeshData(Transform t)
        {
            Mesh m = null;
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf != null)
            {
                m = mf.sharedMesh;
                return new MeshData(m.vertices, m.triangles, m.bounds);
            }
            else
            {
                SkinnedMeshRenderer smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr != null)
                {
                    m = new Mesh();
                    smr.BakeMesh(m);
                    MeshData md = new MeshData(m.vertices, m.triangles, m.bounds);

                    UnityEngine.Object.Destroy(m);      //ensure that no memory is left over
                    return md;
                }
            }
            return null;
        }

        private List<MeshData> CreateMeshListFromTransforms(ref List<Transform> meshTransforms)
        {
            List<MeshData> meshList = new List<MeshData>();
            List<Transform> validTransformList = new List<Transform>();

            Bounds rendererBounds = this.part.GetPartOverallMeshBoundsInBasis(part.partTransform.worldToLocalMatrix);
            Bounds colliderBounds = this.part.GetPartColliderBoundsInBasis(part.partTransform.worldToLocalMatrix);

            bool cantUseColliders = true;

            //Voxelize colliders
            if (forceUseColliders || (rendererBounds.size.x * rendererBounds.size.z < colliderBounds.size.x * colliderBounds.size.z * 1.6f && rendererBounds.size.y < colliderBounds.size.y * 1.2f && (rendererBounds.center - colliderBounds.center).magnitude < 0.3f))
            {
                foreach (Transform t in meshTransforms)
                {
                    MeshData md = GetColliderMeshData(t);
                    if (md == null)
                        continue;

                    meshList.Add(md);
                    validTransformList.Add(t);
                    cantUseColliders = false;
                }
            }

            //Voxelize Everything
            if (cantUseColliders || forceUseMeshes)       //in this case, voxelize _everything_
            {
                foreach (Transform t in meshTransforms)
                {
                    MeshData md = GetVisibleMeshData(t);
                    if (md == null)
                        continue;

                    meshList.Add(md);
                    validTransformList.Add(t);
                }
            }

            if (part.Modules.Contains("ModuleJettison"))
            {
                ModuleJettison[] jettisons = part.GetComponents<ModuleJettison>();
                foreach (ModuleJettison j in jettisons)
                {
                    if (j.isJettisoned || j.jettisonTransform == null)
                        continue;

                    Transform t = j.jettisonTransform;
                    if (t.gameObject.activeInHierarchy == false)
                        continue;
                    
                    MeshData md = GetVisibleMeshData(t);
                    if (md == null)
                        continue;

                    meshList.Add(md);
                    validTransformList.Add(t);
                }
            }

            meshTransforms = validTransformList;
            return meshList;
        }

        private static MeshData CreateBoxMeshFromBoxCollider(Vector3 size, Vector3 center)
        {
            List<Vector3> Points = new List<Vector3>();
            List<Vector3> Verts = new List<Vector3>();
            List<int> Tris = new List<int>();

            Vector3 extents = size * 0.5f;

            Points.Add(new Vector3(center.x - extents.x, center.y + extents.y, center.z - extents.z));
            Points.Add(new Vector3(center.x + extents.x, center.y + extents.y, center.z - extents.z));
            Points.Add(new Vector3(center.x + extents.x, center.y - extents.y, center.z - extents.z));
            Points.Add(new Vector3(center.x - extents.x, center.y - extents.y, center.z - extents.z));
            Points.Add(new Vector3(center.x + extents.x, center.y + extents.y, center.z + extents.z));
            Points.Add(new Vector3(center.x - extents.x, center.y + extents.y, center.z + extents.z));
            Points.Add(new Vector3(center.x - extents.x, center.y - extents.y, center.z + extents.z));
            Points.Add(new Vector3(center.x + extents.x, center.y - extents.y, center.z + extents.z));
            // Front plane
            Verts.Add(Points[0]); Verts.Add(Points[1]); Verts.Add(Points[2]); Verts.Add(Points[3]);
            // Back plane
            Verts.Add(Points[4]); Verts.Add(Points[5]); Verts.Add(Points[6]); Verts.Add(Points[7]);
            // Left plane
            Verts.Add(Points[5]); Verts.Add(Points[0]); Verts.Add(Points[3]); Verts.Add(Points[6]);
            // Right plane
            Verts.Add(Points[1]); Verts.Add(Points[4]); Verts.Add(Points[7]); Verts.Add(Points[2]);
            // Top plane
            Verts.Add(Points[5]); Verts.Add(Points[4]); Verts.Add(Points[1]); Verts.Add(Points[0]);
            // Bottom plane
            Verts.Add(Points[3]); Verts.Add(Points[2]); Verts.Add(Points[7]); Verts.Add(Points[6]);
            // Front Plane
            Tris.Add(0); Tris.Add(1); Tris.Add(2);
            Tris.Add(2); Tris.Add(3); Tris.Add(0);
            // Back Plane
            Tris.Add(4); Tris.Add(5); Tris.Add(6);
            Tris.Add(6); Tris.Add(7); Tris.Add(4);
            // Left Plane
            Tris.Add(8); Tris.Add(9); Tris.Add(10);
            Tris.Add(10); Tris.Add(11); Tris.Add(8);
            // Right Plane
            Tris.Add(12); Tris.Add(13); Tris.Add(14);
            Tris.Add(14); Tris.Add(15); Tris.Add(12);
            // Top Plane
            Tris.Add(16); Tris.Add(17); Tris.Add(18);
            Tris.Add(18); Tris.Add(19); Tris.Add(16);
            // Bottom Plane
            Tris.Add(20); Tris.Add(21); Tris.Add(22);
            Tris.Add(22); Tris.Add(23); Tris.Add(20);

            MeshData mesh = new MeshData(Verts.ToArray(), Tris.ToArray(), new Bounds(center, size));

            return mesh;
        }

        public void OnRescale(TweakScale.ScalingFactor factor)
        {
            if (meshDataList == null)
                return;

            Rescale(factor.absolute.linear / currentScaleFactor * Vector3.one);
        }

        public void RC_Rescale(Vector3 relativeRescaleFactor)
        {
            Rescale(relativeRescaleFactor);             //this is currently just a wrapper, in the future if Rescale changes this can change to maintain compatibility
        }           

        public void Rescale(Vector3 relativeRescaleFactor)
        {
            Matrix4x4 transformMatrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, relativeRescaleFactor);
            if (HighLogic.LoadedSceneIsFlight)
                transformMatrix = vessel.transform.worldToLocalMatrix * transformMatrix;
            else
                transformMatrix = EditorLogic.RootPart.partTransform.worldToLocalMatrix * transformMatrix;

            currentScaleFactor *= relativeRescaleFactor.x;

            UpdateTransformMatrixList(transformMatrix);
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);
            if(node.HasValue("forceUseColliders"))
            {
                bool.TryParse(node.GetValue("forceUseColliders"), out forceUseColliders);
                _ready = false;
            }
            if (node.HasValue("forceUseMeshes"))
            {
                bool.TryParse(node.GetValue("forceUseMeshes"), out forceUseMeshes);
                _ready = false;
            }
        }
    }
}
