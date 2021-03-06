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
using System.Linq;
using System.Threading;
using UnityEngine;
using FerramAerospaceResearch.FARPartGeometry;
using FerramAerospaceResearch.FARPartGeometry.GeometryModification;

namespace FerramAerospaceResearch.FARAeroComponents
{
    class VehicleAerodynamics
    {
        VehicleVoxel _voxel = null;
        VoxelCrossSection[] _vehicleCrossSection = new VoxelCrossSection[1];
        int _voxelCount;

        double _length = 0;
        public double Length
        {
            get { return _length; }
        }

        double _maxCrossSectionArea = 0;
        public double MaxCrossSectionArea
        {
            get { return _maxCrossSectionArea; }
        }

        bool _calculationCompleted = false;
        public bool CalculationCompleted
        {
            get { return _calculationCompleted; }
        }

        double _sonicDragArea;
        public double SonicDragArea
        {
            get { return _sonicDragArea; }
        }

        double _criticalMach;
        public double CriticalMach
        {
            get { return _criticalMach; }
        }

        Matrix4x4 _worldToLocalMatrix, _localToWorldMatrix;

        Vector3d _voxelLowerRightCorner;
        double _voxelElementSize;
        double _sectionThickness;
        public double SectionThickness
        {
            get { return _sectionThickness; }
        }

        Vector3 _vehicleMainAxis;
        List<Part> _vehiclePartList;

        List<GeometryPartModule> _currentGeoModules;
        Dictionary<Part, PartTransformInfo> _partWorldToLocalMatrix = new Dictionary<Part, PartTransformInfo>();
        Dictionary<FARAeroPartModule, FARAeroPartModule.ProjectedArea> _moduleAndAreas = new Dictionary<FARAeroPartModule, FARAeroPartModule.ProjectedArea>();

        List<FARAeroPartModule> _currentAeroModules;
        List<FARAeroPartModule> _newAeroModules;

        List<FARAeroPartModule> _currentUnusedAeroModules;
        List<FARAeroPartModule> _newUnusedAeroModules;

        List<FARAeroSection> _currentAeroSections;
        List<FARAeroSection> _newAeroSections;

        List<ferram4.FARWingAerodynamicModel> _legacyWingModels = new List<ferram4.FARWingAerodynamicModel>();

        int validSectionCount;
        int firstSection;

        bool visualizing = false;
        bool voxelizing = false;

        #region UpdateAeroData
        //Used by other classes to update their aeroModule and aeroSection lists
        //When these functions fire, all the data that was once restricted to the voxelization thread is passed over to the main unity thread

        public void GetNewAeroData(out List<FARAeroPartModule> aeroModules, out List<FARAeroPartModule> unusedAeroModules, out List<FARAeroSection> aeroSections, out List<ferram4.FARWingAerodynamicModel> legacyWingModel)
        {
            _calculationCompleted = false;
            aeroModules = _currentAeroModules = _newAeroModules;

            aeroSections = _currentAeroSections = _newAeroSections;

            unusedAeroModules = _currentUnusedAeroModules = _newUnusedAeroModules;


            legacyWingModel = LEGACY_UpdateWingAerodynamicModels();
        }

        public void GetNewAeroData(out List<FARAeroPartModule> aeroModules, out List<FARAeroSection> aeroSections)
        {
            _calculationCompleted = false;
            aeroModules = _currentAeroModules = _newAeroModules;

            aeroSections = _currentAeroSections = _newAeroSections;

            LEGACY_UpdateWingAerodynamicModels();
        }

        private List<ferram4.FARWingAerodynamicModel> LEGACY_UpdateWingAerodynamicModels()
        {
            _legacyWingModels.Clear();
            for (int i = 0; i < _currentAeroModules.Count; i++)
            {
                Part p = _currentAeroModules[i].part;
                if (!p)
                    continue;
                if (p.Modules.Contains("FARWingAerodynamicModel"))
                {
                    ferram4.FARWingAerodynamicModel w = (ferram4.FARWingAerodynamicModel)p.Modules["FARWingAerodynamicModel"];
                    if (w)
                    {
                        w.isShielded = false;
                        w.NUFAR_ClearExposedAreaFactor();
                        _legacyWingModels.Add(w);
                    }
                }
                else if (p.Modules.Contains("FARControllableSurface"))
                {
                    ferram4.FARWingAerodynamicModel w = (ferram4.FARWingAerodynamicModel)p.Modules["FARControllableSurface"];
                    if (w)
                    {
                        w.isShielded = false;
                        w.NUFAR_ClearExposedAreaFactor();
                        _legacyWingModels.Add(w);
                    }
                }
            }

            /*for (int i = 0; i < _currentAeroSections.Count; i++)
            {
                FARAeroSection sect = _currentAeroSections[i];
                sect.LEGACY_SetLiftForFARWingAerodynamicModel();
            }*/

            for (int i = 0; i < _legacyWingModels.Count; i++)
            {
                ferram4.FARWingAerodynamicModel w = _legacyWingModels[i];
                w.NUFAR_CalculateExposedAreaFactor();
            } 

            for (int i = 0; i < _legacyWingModels.Count; i++)
            {
                ferram4.FARWingAerodynamicModel w = _legacyWingModels[i];
                w.NUFAR_SetExposedAreaFactor();
            }
            return _legacyWingModels;
        }
        #endregion

        #region GetFunctions

        //returns various data for use in displaying outside this class

        public Matrix4x4 VoxelAxisToLocalCoordMatrix()
        {
            return Matrix4x4.TRS(Vector3.zero, Quaternion.FromToRotation(_vehicleMainAxis, Vector3.up), Vector3.one);
        }

        public double FirstSectionXOffset()
        {
            double offset = Vector3d.Dot(_vehicleMainAxis, _voxelLowerRightCorner);
            offset += firstSection * _sectionThickness;

            return offset;
        }

        public double[] GetCrossSectionAreas()
        {
            double[] areas = new double[validSectionCount];
            return GetCrossSectionAreas(areas);
        }

        public double[] GetCrossSectionAreas(double[] areas)
        {
            for (int i = firstSection; i < validSectionCount + firstSection; i++)
                areas[i - firstSection] = _vehicleCrossSection[i].area;

            return areas;
        }

        public double[] GetCrossSection2ndAreaDerivs()
        {
            double[] areaDerivs = new double[validSectionCount];
            return GetCrossSection2ndAreaDerivs(areaDerivs);
        }

        public double[] GetCrossSection2ndAreaDerivs(double[] areaDerivs)
        {
            for (int i = firstSection; i < validSectionCount + firstSection; i++)
                areaDerivs[i - firstSection] = _vehicleCrossSection[i].secondAreaDeriv;

            return areaDerivs;
        }

        #endregion

        #region VoxelDebug
        //Handling for display of debug voxels

        private void ClearDebugVoxel()
        {
            _voxel.ClearVisualVoxels();
            visualizing = false;
        }

        private void DisplayDebugVoxels(Matrix4x4 localToWorldMatrix)
        {
            _voxel.VisualizeVoxel(localToWorldMatrix);
            visualizing = true;
        }

        public void DebugVisualizeVoxels(Matrix4x4 localToWorldMatrix)
        {
            if (visualizing)
                ClearDebugVoxel();
            else
                DisplayDebugVoxels(localToWorldMatrix);
        }

        #endregion

        #region Voxelization

        //This function will attempt to voxelize the vessel, as long as it isn't being voxelized currently all data that is on the Unity thread should be processed here before being passed to the other threads
        public bool TryVoxelUpdate(Matrix4x4 worldToLocalMatrix, Matrix4x4 localToWorldMatrix, int voxelCount, List<Part> vehiclePartList, List<GeometryPartModule> currentGeoModules, bool updateGeometryPartModules = true)
        {
            bool returnVal = false;
            if (Monitor.TryEnter(this))
            {
                try
                {
                    if (voxelizing)
                    {
                        returnVal = false;
                    }
                    else
                    {
                        _voxelCount = voxelCount;

                        this._worldToLocalMatrix = worldToLocalMatrix;
                        this._localToWorldMatrix = localToWorldMatrix;
                        this._vehiclePartList = vehiclePartList;
                        this._currentGeoModules = currentGeoModules;

                        _partWorldToLocalMatrix.Clear();

                        for (int i = 0; i < _currentGeoModules.Count; i++)
                        {
                            GeometryPartModule g = _currentGeoModules[i];
                            _partWorldToLocalMatrix.Add(g.part, new PartTransformInfo(g.part.partTransform));
                            if (updateGeometryPartModules)
                                g.UpdateTransformMatrixList(_worldToLocalMatrix);
                        }

                        this._vehicleMainAxis = CalculateVehicleMainAxis();

                        visualizing = false;

                        if (_voxel != null)
                        {
                            _voxel.CleanupVoxel();
                        }

                        voxelizing = true;
                        ThreadPool.QueueUserWorkItem(CreateVoxel, updateGeometryPartModules);
                        returnVal = true;
                    }
                }
                finally
                {
                    Monitor.Exit(this);
                }
            }
            
            return returnVal;
        }

        //And this actually creates the voxel and then begins the aero properties determination
        private void CreateVoxel(object updateGeometryPartModules)
        {
            lock (this)
            {
                try
                {

                    _voxel = new VehicleVoxel(_vehiclePartList, _currentGeoModules, _voxelCount);
                    if (_vehicleCrossSection.Length < _voxel.MaxArrayLength)
                        _vehicleCrossSection = _voxel.EmptyCrossSectionArray;

                    _voxelLowerRightCorner = _voxel.LocalLowerRightCorner;
                    _voxelElementSize = _voxel.ElementSize;

                    CalculateVesselAeroProperties();
                    _calculationCompleted = true;

                    //                    voxelizing = false;
                }

                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                finally
                {
                    if (HighLogic.LoadedSceneIsFlight && _voxel != null)
                    {
                        _voxel.CleanupVoxel();
                        _voxel = null;
                    }
                    voxelizing = false;
                }
            }
        }

        #endregion


        private Vector3 CalculateVehicleMainAxis()
        {
            Vector3 axis = Vector3.zero;
            //Vector3 notAxis = Vector3.zero;
            HashSet<Part> hitParts = new HashSet<Part>();

            for (int i = 0; i < _vehiclePartList.Count; i++)
            {
                Part p = _vehiclePartList[i];

                if (p == null || hitParts.Contains(p))
                    continue;

                hitParts.Add(p);

                Vector3 candVector = p.partTransform.up;
                if (p.Modules.Contains("ModuleResourceIntake"))      //intakes are probably pointing in the direction we're gonna be going in
                {
                    ModuleResourceIntake intake = (ModuleResourceIntake)p.Modules["ModuleResourceIntake"];
                    Transform intakeTrans = p.FindModelTransform(intake.intakeTransformName);
                    if ((object)intakeTrans != null)
                        candVector = intakeTrans.forward;
                }
                else if (p.Modules.Contains("FARWingAerodynamicModel") || p.Modules.Contains("FARControllableSurface"))      //aggregate wings for later calc...
                {
                    continue;
                /*    Vector3 notCandVector =  _worldToLocalMatrix.MultiplyVector(p.partTransform.forward);
                    notCandVector.x = Math.Abs(notCandVector.x);
                    notCandVector.y = Math.Abs(notCandVector.y);
                    notCandVector.z = Math.Abs(notCandVector.z);
                    notAxis += notCandVector;*/
                }
                for (int j = 0; j < p.symmetryCounterparts.Count; j++)
                {
                    Part q = p.symmetryCounterparts[j];

                    if (q == null || hitParts.Contains(q))
                        continue;

                    hitParts.Add(q);

                    if (q.Modules.Contains("ModuleResourceIntake"))      //intakes are probably pointing in the direction we're gonna be going in
                    {
                        ModuleResourceIntake intake = (ModuleResourceIntake)q.Modules["ModuleResourceIntake"];
                        Transform intakeTrans = q.FindModelTransform(intake.intakeTransformName);
                        if ((object)intakeTrans != null)
                            candVector += intakeTrans.forward;
                    }
                    /*else if (q.Modules.Contains("FARWingAerodynamicModel") || q.Modules.Contains("FARControllableSurface"))      //aggregate wings for later calc...
                    {
                        Vector3 notCandVector = _worldToLocalMatrix.MultiplyVector(p.partTransform.forward);
                        notCandVector.x = Math.Abs(notCandVector.x);
                        notCandVector.y = Math.Abs(notCandVector.y);
                        notCandVector.z = Math.Abs(notCandVector.z);
                        notAxis += notCandVector;
                    }*/
                    else
                        candVector += q.partTransform.up;
                }

                candVector = _worldToLocalMatrix.MultiplyVector(candVector);
                candVector.x = Math.Abs(candVector.x);
                candVector.y = Math.Abs(candVector.y);
                candVector.z = Math.Abs(candVector.z);

                axis += candVector * p.mass * (1 + p.symmetryCounterparts.Count);    //scale part influence by approximate size
            }
            /*float perpTest = Math.Abs(Vector3.Dot(axis, notAxis));

            if (perpTest > 0.3)
            {
                axis = Vector3.Cross(axis, notAxis);
                axis = Vector3.Cross(axis, notAxis);        //this shoudl result in an axis perpendicular to notAxis
                //axis.Normalize();
            }*/


            float dotProdX, dotProdY, dotProdZ;

            dotProdX = Math.Abs(Vector3.Dot(axis, Vector3.right));
            dotProdY = Math.Abs(Vector3.Dot(axis, Vector3.up));
            dotProdZ = Math.Abs(Vector3.Dot(axis, Vector3.forward));

            if (dotProdY > 2 * dotProdX && dotProdY > 2 * dotProdZ)
                return Vector3.up;

            if (dotProdX > 2 * dotProdY && dotProdX > 2 * dotProdZ)
                return Vector3.right;

            if (dotProdZ > 2 * dotProdX && dotProdZ > 2 * dotProdY)
                return Vector3.forward;

            //Otherwise, now we need to use axis, since it's obviously not close to anything else


            return axis.normalized;
        }

        //Smooths out area and area 2nd deriv distributions to deal with noise in the representation
        unsafe void GaussianSmoothCrossSections(VoxelCrossSection[] vehicleCrossSection, double stdDevCutoff, double lengthPercentFactor, double sectionThickness, double length, int frontIndex, int backIndex, int areaSmoothingIterations, int derivSmoothingIterations)
        {
            double stdDev = length * lengthPercentFactor;
            int numVals = (int)Math.Ceiling(stdDevCutoff * stdDev / sectionThickness);

            if (numVals <= 1)
                return;

            double* gaussianFactors = stackalloc double[numVals];
            double* prevUncorrectedVals = stackalloc double[numVals];
            double* futureUncorrectedVals = stackalloc double[numVals - 1];


/*            double[] gaussianFactors = new double[numVals];
            double[] prevUncorrectedVals = new double[numVals];
            double[] futureUncorrectedVals = new double[numVals - 1];*/

            double invVariance = 1 / (stdDev * stdDev);

            //calculate Gaussian factors for each of the points that will be hit
            for (int i = 0; i < numVals; i++)
            {
                double factor = (i * sectionThickness);
                factor *= factor;
                gaussianFactors[i] = Math.Exp(-0.5 * factor * invVariance);
            }

            //then sum them up...
            double sum = 0;
            for (int i = 0; i < numVals; i++)
                if (i == 0)
                    sum += gaussianFactors[i];
                else
                    sum += 2 * gaussianFactors[i];

            double invSum = 1 / sum;    //and then use that to normalize the factors

            for (int i = 0; i < numVals; i++)
            {
                gaussianFactors[i] *= invSum;
            }

            //first smooth the area itself.  This has a greater effect on the 2nd deriv due to the effect of noise on derivatives
            for (int j = 0; j < areaSmoothingIterations; j++)
            {
                for (int i = 0; i < numVals; i++)
                    prevUncorrectedVals[i] = 0;     //set all the vals to 0 to prevent screwups between iterations

                for (int i = frontIndex; i <= backIndex; i++)       //area smoothing pass
                {
                    for (int k = numVals - 1; k > 0; k--)
                    {
                        prevUncorrectedVals[k] = prevUncorrectedVals[k - 1];        //shift prev vals down
                    }
                    double curValue = vehicleCrossSection[i].area;
                    prevUncorrectedVals[0] = curValue;       //and set the central value


                    for (int k = 0; k < numVals - 1; k++)          //update future vals
                    {
                        if (i + k < backIndex)
                            futureUncorrectedVals[k] = vehicleCrossSection[i + k + 1].area;
                        else
                            futureUncorrectedVals[k] = 0;
                    }
                    curValue = 0;       //zero for coming calculations...

                    double borderScaling = 1;      //factor to correct for the 0s lurking at the borders of the curve...

                    for (int k = 0; k < numVals; k++)
                    {
                        double val = prevUncorrectedVals[k];
                        double gaussianFactor = gaussianFactors[k];

                        curValue += gaussianFactor * val;        //central and previous values;
                        if (val == 0)
                            borderScaling -= gaussianFactor;
                    }
                    for (int k = 0; k < numVals - 1; k++)
                    {
                        double val = futureUncorrectedVals[k];
                        double gaussianFactor = gaussianFactors[k + 1];

                        curValue += gaussianFactor * val;      //future values
                        if (val == 0)
                            borderScaling -= gaussianFactor;
                    }
                    if (borderScaling > 0)
                        curValue /= borderScaling;      //and now all of the 0s beyond the edge have been removed

                    vehicleCrossSection[i].area = curValue;
                }
            }

            //2nd derivs must be recalculated now using the adjusted areas
            double denom = sectionThickness;
            denom *= denom;
            denom = 0.0625 / denom;

            for (int i = frontIndex; i <= backIndex; i++)       //calculate 2nd derivs, raw
            {
                double areaM3, areaM2, areaM1, area0, areaP1, areaP2, areaP3;

                double areaSecondDeriv;

                if(i - frontIndex < 3)     //N5 forward difference for frontIndex
                {
                    areaM2 = vehicleCrossSection[i].area;
                    area0 = vehicleCrossSection[i + 2].area;
                    areaP2 = vehicleCrossSection[i + 4].area;

                    areaSecondDeriv = (areaM2 + areaP2) - 2 * area0;
                    areaSecondDeriv *= denom * 4;
                }
                else if (backIndex - i < 3) //N5 backward difference for backIndex
                {
                    areaM2 = vehicleCrossSection[i - 4].area;
                    area0 = vehicleCrossSection[i - 2].area;
                    areaP2 = vehicleCrossSection[i].area;

                    areaSecondDeriv = (areaM2 + areaP2) - 2 * area0;
                    areaSecondDeriv *= denom * 4;
                }
                else                     //N7 central difference for all others
                {
                    areaM3 = vehicleCrossSection[i - 3].area;
                    areaM2 = vehicleCrossSection[i - 2].area;
                    areaM1 = vehicleCrossSection[i - 1].area;
                    area0 = vehicleCrossSection[i].area;
                    areaP1 = vehicleCrossSection[i + 1].area;
                    areaP2 = vehicleCrossSection[i + 2].area;
                    areaP3 = vehicleCrossSection[i + 3].area;

                    areaSecondDeriv = (areaM3 + areaP3) + 2 * (areaM2 + areaP2) - (areaM1 + areaP1) - 4 * area0;
                    areaSecondDeriv *= denom;
                }

                vehicleCrossSection[i].secondAreaDeriv = areaSecondDeriv;
            }

            //and now smooth the derivs
            for (int j = 0; j < derivSmoothingIterations; j++)
            {
                for (int i = 0; i < numVals; i++)
                    prevUncorrectedVals[i] = 0;     //set all the vals to 0 to prevent screwups between iterations

                for (int i = frontIndex; i <= backIndex; i++)       //deriv smoothing pass
                {
                    for (int k = numVals - 1; k > 0; k--)
                    {
                        prevUncorrectedVals[k] = prevUncorrectedVals[k - 1];        //shift prev vals down
                    }
                    double curValue = vehicleCrossSection[i].secondAreaDeriv;
                    prevUncorrectedVals[0] = curValue;       //and set the central value


                    for (int k = 0; k < numVals - 1; k++)          //update future vals
                    {
                        if (i + k < backIndex)
                            futureUncorrectedVals[k] = vehicleCrossSection[i + k + 1].secondAreaDeriv;
                        else
                            futureUncorrectedVals[k] = 0;
                    }
                    curValue = 0;       //zero for coming calculations...

                    double borderScaling = 1;      //factor to correct for the 0s lurking at the borders of the curve...

                    for (int k = 0; k < numVals; k++)
                    {
                        double val = prevUncorrectedVals[k];
                        double gaussianFactor = gaussianFactors[k];

                        curValue += gaussianFactor * val;        //central and previous values;
                        if (val == 0)
                            borderScaling -= gaussianFactor;
                    }
                    for (int k = 0; k < numVals - 1; k++)
                    {
                        double val = futureUncorrectedVals[k];
                        double gaussianFactor = gaussianFactors[k + 1];

                        curValue += gaussianFactor * val;      //future values
                        if (val == 0)
                            borderScaling -= gaussianFactor;
                    }
                    if (borderScaling > 0)
                        curValue /= borderScaling;      //and now all of the 0s beyond the edge have been removed

                    vehicleCrossSection[i].secondAreaDeriv = curValue;
                }
            }
        }

        unsafe void AdjustCrossSectionForAirDucting(VoxelCrossSection[] vehicleCrossSection, List<GeometryPartModule> geometryModules)
        {
            List<ICrossSectionAdjuster> forwardFacingAdjustments, rearwardFacingAdjustments;
            forwardFacingAdjustments = new List<ICrossSectionAdjuster>();
            rearwardFacingAdjustments = new List<ICrossSectionAdjuster>();

            double* areaAdjustment = stackalloc double[vehicleCrossSection.Length];

            for (int i = 0; i < geometryModules.Count; i++)
            {
                GeometryPartModule g = geometryModules[i];
                g.GetICrossSectionAdjusters(forwardFacingAdjustments, rearwardFacingAdjustments, _worldToLocalMatrix, _vehicleMainAxis);
            }

            double intakeArea = 0;
            double totalEngineThrust = 0;

            for (int i = 0; i < forwardFacingAdjustments.Count; i++)    //get all forward facing engines / intakes
            {
                ICrossSectionAdjuster adjuster = forwardFacingAdjustments[i];
                if (adjuster is AirbreathingEngineCrossSectonAdjuster)
                    totalEngineThrust += ((AirbreathingEngineCrossSectonAdjuster)adjuster).EngineModule.maxThrust;
                if (adjuster is IntakeCrossSectionAdjuster)
                    intakeArea += adjuster.AreaRemovedFromCrossSection();
            }

            Dictionary<Part, double> adjusterAreaPerVoxelDict = new Dictionary<Part, double>();
            Dictionary<Part, ICrossSectionAdjuster> adjusterPartDict = new Dictionary<Part, ICrossSectionAdjuster>();
            if (intakeArea > 0 && totalEngineThrust > 0)        //if they exist, go through the calculations
            {
                double engineAreaPerUnitThrust = intakeArea / totalEngineThrust;        //figure how much intake area per thrust so that it can be divided up per engine

                for (int i = 0; i < forwardFacingAdjustments.Count; i++)
                {
                    ICrossSectionAdjuster adjuster = forwardFacingAdjustments[i];
                    if (adjuster is AirbreathingEngineCrossSectonAdjuster)
                    {
                        AirbreathingEngineCrossSectonAdjuster engineAdjuster = (AirbreathingEngineCrossSectonAdjuster)adjuster;
                        engineAdjuster.CalculateExitArea(engineAreaPerUnitThrust);
                    }
                }


                for (int i = 0; i < vehicleCrossSection.Length; i++)
                {
                    for (int j = 0; j < forwardFacingAdjustments.Count; j++)
                    {
                        ICrossSectionAdjuster adjuster = forwardFacingAdjustments[j];
                        VoxelCrossSection.SideAreaValues val;
                        Part p = adjuster.GetPart();
                        if (vehicleCrossSection[i].partSideAreaValues.TryGetValue(p, out val))
                        {
                            double currentVal;
                            if (adjusterAreaPerVoxelDict.TryGetValue(p, out currentVal))
                            {
                                if (val.crossSectionalAreaCount > currentVal)
                                    adjusterAreaPerVoxelDict[p] = val.crossSectionalAreaCount;
                            }
                            else
                                adjusterAreaPerVoxelDict[p] = val.crossSectionalAreaCount;
                        }
                    }
                }


                for (int i = 0; i < forwardFacingAdjustments.Count; i++)
                {
                    ICrossSectionAdjuster adjuster = forwardFacingAdjustments[i];
                    double area = adjuster.AreaRemovedFromCrossSection();
                    double tmp = adjusterAreaPerVoxelDict[adjuster.GetPart()];

                    adjuster.SetCrossSectionAreaCountOffset(tmp);
                    adjusterAreaPerVoxelDict[adjuster.GetPart()] = area / tmp;
                    adjusterPartDict.Add(adjuster.GetPart(), adjuster);
                }

                Dictionary<Part, int> partCrossSectionIndexDict = new Dictionary<Part, int>();
                List<CrossSectionAdjustData> partAdjustmentList = new List<CrossSectionAdjustData>();

                for (int i = 0; i < vehicleCrossSection.Length; i++)
                {
                    double area = 0;
                    foreach (KeyValuePair<Part, VoxelCrossSection.SideAreaValues> partAreaPair in vehicleCrossSection[i].partSideAreaValues)
                    {
                        double areaPerVoxel;
                        Part p = partAreaPair.Key;
                        if (adjusterAreaPerVoxelDict.TryGetValue(p, out areaPerVoxel))
                        {
                            double areaRemoved = areaPerVoxel * (partAreaPair.Value.crossSectionalAreaCount - adjusterPartDict[p].GetCrossSectionAreaCountOffset());
                            int currentIndex;

                            if (partCrossSectionIndexDict.TryGetValue(p, out currentIndex))
                            {
                                CrossSectionAdjustData currentData = partAdjustmentList[currentIndex];
                                double currentVal = currentData.activeAreaRemoved;
                                currentData.lastIndex = i;
                                currentData.counter++;
                                if (Math.Abs(areaRemoved) > Math.Abs(currentVal))
                                {
                                    currentData.activeAreaRemoved = areaRemoved;
                                }
                                partAdjustmentList[currentIndex] = currentData;
                            }
                            else
                            {
                                partCrossSectionIndexDict[p] = partAdjustmentList.Count;
                                partAdjustmentList.Add(new CrossSectionAdjustData(areaRemoved, i));
                            }
                        }
                    }

                    foreach (KeyValuePair<Part, int> partIndexPair in partCrossSectionIndexDict)
                    {
                        CrossSectionAdjustData data = partAdjustmentList[partIndexPair.Value];
                        if(data.lastIndex < i - 2 && data.lastIndex != -1)
                        {
                            data.lastIndex = -1;
                            data.activeAreaRemoved = adjusterPartDict[partIndexPair.Key].AreaRemovedFromCrossSection();

                            if (adjusterPartDict[partIndexPair.Key].GetCrossSectionAreaCountOffset() != 0)
                                data.activeAreaRemoved *= -1;

                            partAdjustmentList[partIndexPair.Value] = data;
                            Debug.Log(partIndexPair.Key.partInfo.title + " " + data.counter);
                        }
                        area -= data.activeAreaRemoved;

                    }
                    areaAdjustment[i] += area;
                    //vehicleCrossSection[i].area = Math.Max(0.25 * vehicleCrossSection[i].area, area);
                }
            }
            
            /*intakeArea = 0;
            totalEngineThrust = 0;

            for (int i = 0; i < rearwardFacingAdjustments.Count; i++)
            {
                ICrossSectionAdjuster adjuster = rearwardFacingAdjustments[i];
                if (adjuster is AirbreathingEngineCrossSectonAdjuster)
                    totalEngineThrust += ((AirbreathingEngineCrossSectonAdjuster)adjuster).EngineModule.maxThrust;
                if (adjuster is IntakeCrossSectionAdjuster)
                    intakeArea += adjuster.AreaRemovedFromCrossSection();
            }
            adjusterAreaPerVoxelDict.Clear();
            adjusterPartDict.Clear();

            if (intakeArea > 0 && totalEngineThrust > 0)
            {

                double engineAreaPerUnitThrust = intakeArea / totalEngineThrust;
                for (int i = 0; i < rearwardFacingAdjustments.Count; i++)
                {
                    ICrossSectionAdjuster adjuster = rearwardFacingAdjustments[i];
                    if (adjuster is AirbreathingEngineCrossSectonAdjuster)
                    {
                        AirbreathingEngineCrossSectonAdjuster engineAdjuster = (AirbreathingEngineCrossSectonAdjuster)adjuster;
                        engineAdjuster.CalculateExitArea(engineAreaPerUnitThrust);
                    }
                }

                
                for (int i = 0; i < vehicleCrossSection.Length; i++)
                {
                    for (int j = 0; j < rearwardFacingAdjustments.Count; j++)
                    {
                        ICrossSectionAdjuster adjuster = rearwardFacingAdjustments[j];
                        VoxelCrossSection.SideAreaValues val;
                        Part p = adjuster.GetPart();
                        if (vehicleCrossSection[i].partSideAreaValues.TryGetValue(p, out val))
                        {
                            double currentVal;
                            if (adjusterAreaPerVoxelDict.TryGetValue(p, out currentVal))
                            {
                                if (val.crossSectionalAreaCount > currentVal)
                                    adjusterAreaPerVoxelDict[p] = val.crossSectionalAreaCount;
                            }
                            else
                                adjusterAreaPerVoxelDict[p] = val.crossSectionalAreaCount;
                        }
                    }
                }

                for (int i = 0; i < rearwardFacingAdjustments.Count; i++)
                {
                    ICrossSectionAdjuster adjuster = rearwardFacingAdjustments[i];
                    double area = adjuster.AreaRemovedFromCrossSection();
                    double tmp = adjusterAreaPerVoxelDict[adjuster.GetPart()];
                    adjusterAreaPerVoxelDict[adjuster.GetPart()] = area / tmp;
                    adjusterPartDict.Add(adjuster.GetPart(), adjuster);
                }

                Dictionary<Part, double> partActiveAreaRemoved = new Dictionary<Part, double>();

                for (int i = vehicleCrossSection.Length - 1; i >= 0; i--)
                {
                    double area = 0;
                    foreach (KeyValuePair<Part, VoxelCrossSection.SideAreaValues> partAreaPair in vehicleCrossSection[i].partSideAreaValues)
                    {
                        double areaPerVoxel;
                        Part p = partAreaPair.Key;
                        if (adjusterAreaPerVoxelDict.TryGetValue(p, out areaPerVoxel))
                        {
                            double areaRemoved = areaPerVoxel * partAreaPair.Value.crossSectionalAreaCount - adjusterPartDict[p].GetCrossSectionAreaOffset();
                            double currentVal;

                            if (partActiveAreaRemoved.TryGetValue(p, out currentVal))
                            {
                                if (Math.Abs(areaRemoved) > Math.Abs(currentVal))
                                    partActiveAreaRemoved[p] = areaRemoved;
                            }
                            else
                                partActiveAreaRemoved[p] = areaRemoved;
                        }
                    }
                    foreach (KeyValuePair<Part, double> partAreaRemovedPair in partActiveAreaRemoved)
                        area -= partAreaRemovedPair.Value;


                    areaAdjustment[i] += area;
                }
            }*/
            for(int i = 0; i < vehicleCrossSection.Length; i++)
            {
                double areaUnchanged = vehicleCrossSection[i].area;
                double areaChanged = areaAdjustment[i];
                if (areaChanged > 0)
                    areaChanged = 0;
                areaChanged += areaUnchanged;

                vehicleCrossSection[i].area = Math.Max(0.15 * areaUnchanged, areaChanged);

            }
        }

        #region Aerodynamics Calculations

        private void CalculateVesselAeroProperties()
        {
            int front, back, numSections;

            _voxel.CrossSectionData(_vehicleCrossSection, _vehicleMainAxis, out front, out back, out _sectionThickness, out _maxCrossSectionArea);

            numSections = back - front;
            _length = _sectionThickness * numSections;

            //AdjustCrossSectionForAirDucting(_vehicleCrossSection, _currentGeoModules);

            GaussianSmoothCrossSections(_vehicleCrossSection, 3, FARSettingsScenarioModule.Settings.gaussianVehicleLengthFractionForSmoothing, _sectionThickness, _length, front, back, FARSettingsScenarioModule.Settings.numAreaSmoothingPasses, FARSettingsScenarioModule.Settings.numDerivSmoothingPasses);

            validSectionCount = numSections;
            firstSection = front;
            double invMaxRadFactor = 1f / Math.Sqrt(_maxCrossSectionArea / Math.PI);

            double finenessRatio = _sectionThickness * numSections * 0.5 * invMaxRadFactor;       //vehicle length / max diameter, as calculated from sect thickness * num sections / (2 * max radius) 

            //skin friction and pressure drag for a body, taken from 1978 USAF Stability And Control DATCOM, Section 4.2.3.1, Paragraph A
            double viscousDragFactor = 0;
            viscousDragFactor = 60 / (finenessRatio * finenessRatio * finenessRatio) + 0.0025 * finenessRatio;     //pressure drag for a subsonic / transonic body due to skin friction
            viscousDragFactor++;

            viscousDragFactor /= (double)numSections;   //fraction of viscous drag applied to each section

            double criticalMachNumber = CalculateCriticalMachNumber(finenessRatio);

            _criticalMach = criticalMachNumber * CriticalMachFactorForUnsmoothCrossSection(_vehicleCrossSection, finenessRatio, _sectionThickness);

            float lowFinenessRatioSubsonicFactor = 1f;
            lowFinenessRatioSubsonicFactor += 1f/(2f * (float)finenessRatio);

            _moduleAndAreas.Clear();
            _newAeroSections = new List<FARAeroSection>();
            List<FARAeroPartModule> includedModules = new List<FARAeroPartModule>();
            List<float> weighting = new List<float>();
            HashSet<FARAeroPartModule> tmpAeroModules = new HashSet<FARAeroPartModule>();
            _sonicDragArea = 0;
            for (int i = 0; i <= numSections; i++)  //index in the cross sections
            {
                int index = i + front;      //index along the actual body

                double prevArea, curArea, nextArea;

                curArea = _vehicleCrossSection[index].area;
                if (i == 0)
                    prevArea = 0;
                else
                    prevArea = _vehicleCrossSection[index - 1].area;
                if (i == numSections)
                    nextArea = 0;
                else
                    nextArea = _vehicleCrossSection[index + 1].area;

                FloatCurve xForcePressureAoA0 = new FloatCurve();
                FloatCurve xForcePressureAoA180 = new FloatCurve();

                FloatCurve xForceSkinFriction = new FloatCurve();

                //Potential and Viscous lift calcs
                float potentialFlowNormalForce;
                if(i == 0)
                    potentialFlowNormalForce = (float)(nextArea - curArea);
                else if(i == numSections)
                    potentialFlowNormalForce = (float)(curArea - prevArea);
                else
                    potentialFlowNormalForce = (float)(nextArea - prevArea) * 0.5f;      //calcualted from area change

                float areaChangeMax = (float)Math.Min(Math.Min(nextArea, prevArea) * 0.1, _length * 0.01);

                float sonicBaseDrag = 0.21f;

                sonicBaseDrag *= potentialFlowNormalForce;    //area base drag acts over

                if (potentialFlowNormalForce > areaChangeMax)
                    potentialFlowNormalForce = areaChangeMax;
                else if (potentialFlowNormalForce < -areaChangeMax)
                    potentialFlowNormalForce = -areaChangeMax;
                else
                    sonicBaseDrag *= Math.Abs(potentialFlowNormalForce / areaChangeMax);      //some scaling for small changes in cross-section

                double flatnessRatio = _vehicleCrossSection[index].flatnessRatio;
                if (flatnessRatio >= 1)
                    sonicBaseDrag /= (float)flatnessRatio;
                else
                    sonicBaseDrag *= (float)flatnessRatio;

                sonicBaseDrag = Math.Abs(sonicBaseDrag);

                Dictionary<Part, VoxelCrossSection.SideAreaValues> includedPartsAndAreas = _vehicleCrossSection[index].partSideAreaValues;

                double surfaceArea = 0;
                foreach (KeyValuePair<Part, VoxelCrossSection.SideAreaValues> pair in includedPartsAndAreas)
                {
                    VoxelCrossSection.SideAreaValues areas = pair.Value;
                    surfaceArea += areas.iN + areas.iP + areas.jN + areas.jP + areas.kN + areas.kP;
                }

                float viscCrossflowDrag = (float)(Math.Sqrt(curArea / Math.PI) * _sectionThickness * 2d);

                xForceSkinFriction.Add(0f, (float)(surfaceArea * viscousDragFactor), 0, 0);   //subsonic incomp visc drag
                xForceSkinFriction.Add(1f, (float)(surfaceArea * viscousDragFactor), 0, 0);   //transonic visc drag
                xForceSkinFriction.Add(2f, (float)surfaceArea, 0, 0);                     //above Mach 1.4, visc is purely surface drag, no pressure-related components simulated

                float sonicWaveDrag = (float)CalculateTransonicWaveDrag(i, index, numSections, front, _sectionThickness, Math.Min(_maxCrossSectionArea * 2, curArea * 16));//Math.Min(maxCrossSectionArea * 0.1, curArea * 0.25));
                sonicWaveDrag *= (float)FARSettingsScenarioModule.Settings.fractionTransonicDrag;     //this is just to account for the higher drag being felt due to the inherent blockiness of the model being used and noise introduced by the limited control over shape and through voxelization
                float hypersonicDragForward = (float)CalculateHypersonicDrag(prevArea, curArea, _sectionThickness);
                float hypersonicDragBackward = (float)CalculateHypersonicDrag(nextArea, curArea, _sectionThickness);

                float hypersonicMomentForward = (float)CalculateHypersonicMoment(prevArea, curArea, _sectionThickness);
                float hypersonicMomentBackward = (float)CalculateHypersonicMoment(nextArea, curArea, _sectionThickness);

                xForcePressureAoA0.Add(35f, hypersonicDragForward, 0f, 0f);
                xForcePressureAoA180.Add(35f, -hypersonicDragBackward, 0f, 0f);

                float sonicAoA0Drag, sonicAoA180Drag;
                if (sonicBaseDrag > 0)      //occurs with increase in area; force applied at 180 AoA
                {
                    xForcePressureAoA0.Add((float)criticalMachNumber, (hypersonicDragForward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);    //hypersonic drag used as a proxy for effects due to flow separation
                    xForcePressureAoA180.Add((float)criticalMachNumber, (sonicBaseDrag * 0.25f - hypersonicDragBackward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);

                    sonicAoA0Drag = sonicWaveDrag + hypersonicDragForward * 0.2f;
                    sonicAoA180Drag = -sonicWaveDrag + sonicBaseDrag -hypersonicDragBackward * 0.2f;
                    //xForcePressureAoA0.Add(1f, sonicWaveDrag + hypersonicDragForward * 0.1f, 0f, 0f);     //positive is force forward; negative is force backward
                    //xForcePressureAoA180.Add(1f, -sonicWaveDrag - hypersonicDragBackward * 0.1f + sonicBaseDrag, 0f, 0f);
                }
                else if (sonicBaseDrag < 0)
                {
                    xForcePressureAoA0.Add((float)criticalMachNumber, (-sonicBaseDrag * 0.25f + hypersonicDragForward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);
                    xForcePressureAoA180.Add((float)criticalMachNumber, (-hypersonicDragBackward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);

                    sonicAoA0Drag = sonicWaveDrag - sonicBaseDrag + hypersonicDragForward * 0.2f;
                    sonicAoA180Drag = -sonicWaveDrag - hypersonicDragBackward * 0.2f;

                    //xForcePressureAoA0.Add(1f, sonicWaveDrag + hypersonicDragForward * 0.1f - sonicBaseDrag, 0f, 0f);     //positive is force forward; negative is force backward
                    //xForcePressureAoA180.Add(1f, -sonicWaveDrag - hypersonicDragBackward * 0.1f, 0f, 0f);
                }
                else
                {
                    xForcePressureAoA0.Add((float)criticalMachNumber, (hypersonicDragForward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);
                    xForcePressureAoA180.Add((float)criticalMachNumber, (-hypersonicDragBackward * 0.4f) * lowFinenessRatioSubsonicFactor, 0f, 0f);

                    sonicAoA0Drag = sonicWaveDrag + hypersonicDragForward * 0.2f;
                    sonicAoA180Drag = -sonicWaveDrag - hypersonicDragBackward * 0.2f;

                    //xForcePressureAoA0.Add(1f, sonicWaveDrag + hypersonicDragForward * 0.1f, 0f, 0f);     //positive is force forward; negative is force backward
                    //xForcePressureAoA180.Add(1f, -sonicWaveDrag - hypersonicDragBackward * 0.1f, 0f, 0f);
                }
                float diffSonicHyperAoA0 = Math.Abs(sonicAoA0Drag) - Math.Abs(hypersonicDragForward);
                float diffSonicHyperAoA180 = Math.Abs(sonicAoA180Drag) - Math.Abs(hypersonicDragBackward);


                xForcePressureAoA0.Add(1f, sonicAoA0Drag, 0, 0);
                xForcePressureAoA180.Add(1f, sonicAoA180Drag, 0, 0);

                xForcePressureAoA0.Add(2f, sonicAoA0Drag * 0.5773503f + (1 - 0.5773503f) * hypersonicDragForward);
                xForcePressureAoA180.Add(2f, sonicAoA180Drag * 0.5773503f - (1 - 0.5773503f) * hypersonicDragBackward);

                xForcePressureAoA0.Add(5f, sonicAoA0Drag * 0.2041242f + (1 - 0.2041242f) * hypersonicDragForward, -0.04252587f * diffSonicHyperAoA0, -0.04252587f * diffSonicHyperAoA0);
                xForcePressureAoA180.Add(5f, sonicAoA180Drag * 0.2041242f - (1 - 0.2041242f) * hypersonicDragBackward, -0.04252587f * diffSonicHyperAoA180, -0.04252587f * diffSonicHyperAoA180);

                xForcePressureAoA0.Add(10f, sonicAoA0Drag * 0.1005038f + (1 - 0.1005038f) * hypersonicDragForward, -0.0101519f * diffSonicHyperAoA0, -0.0101519f * diffSonicHyperAoA0);
                xForcePressureAoA180.Add(10f, sonicAoA180Drag * 0.1005038f - (1 - 0.1005038f) * hypersonicDragBackward, -0.0101519f * diffSonicHyperAoA180, -0.0101519f * diffSonicHyperAoA180);

                Vector3 xRefVector;
                if (index == front || index == back)
                    xRefVector = _vehicleMainAxis;
                else
                {
                    xRefVector = (Vector3)(_vehicleCrossSection[index - 1].centroid - _vehicleCrossSection[index + 1].centroid);
                    Vector3 offMainAxisVec = Vector3.ProjectOnPlane(xRefVector, _vehicleMainAxis);
                    float tanAoA = offMainAxisVec.magnitude / (2f * (float)_sectionThickness);
                    if (tanAoA > 0.17632698070846497347109038686862f)
                    {
                        offMainAxisVec.Normalize();
                        offMainAxisVec *= 0.17632698070846497347109038686862f;      //max acceptable is 10 degrees
                        xRefVector = _vehicleMainAxis + offMainAxisVec;
                    }
                    xRefVector.Normalize();
                }


                Vector3 nRefVector = Matrix4x4.TRS(Vector3.zero, Quaternion.FromToRotation(_vehicleMainAxis, xRefVector), Vector3.one).MultiplyVector(_vehicleCrossSection[index].flatNormalVector);
                
                Vector3 centroid = _localToWorldMatrix.MultiplyPoint3x4(_vehicleCrossSection[index].centroid);
                xRefVector = _localToWorldMatrix.MultiplyVector(xRefVector);
                nRefVector = _localToWorldMatrix.MultiplyVector(nRefVector);


                float weightingFactor = 0;

                //weight the forces applied to each part
                foreach (KeyValuePair<Part, VoxelCrossSection.SideAreaValues> pair in includedPartsAndAreas)
                {
                    Part key = pair.Key;
                    if (key == null)
                        continue;

                    if (!key.Modules.Contains("FARAeroPartModule"))
                        continue;

                    FARAeroPartModule m = (FARAeroPartModule)key.Modules["FARAeroPartModule"];
                    if (m != null)
                        includedModules.Add(m);

                    if (_moduleAndAreas.ContainsKey(m))
                        _moduleAndAreas[m] += pair.Value;
                    else
                        _moduleAndAreas[m] = new FARAeroPartModule.ProjectedArea() + pair.Value;

                    weightingFactor += (float)pair.Value.exposedAreaCount;
                    weighting.Add((float)pair.Value.exposedAreaCount);
                }
                weightingFactor = 1 / weightingFactor;
                for (int j = 0; j < includedModules.Count; j++)
                {
                    weighting[j] *= weightingFactor;
                }


                FARAeroSection section = new FARAeroSection(xForcePressureAoA0, xForcePressureAoA180, xForceSkinFriction, potentialFlowNormalForce, viscCrossflowDrag
                    ,viscCrossflowDrag / (float)(_sectionThickness), (float)flatnessRatio, hypersonicMomentForward, hypersonicMomentBackward,
                    centroid, xRefVector, nRefVector, _localToWorldMatrix, _vehicleMainAxis, includedModules, includedPartsAndAreas, weighting, _partWorldToLocalMatrix);

                _newAeroSections.Add(section);

                for (int j = 0; j < includedModules.Count; j++)
                {
                    FARAeroPartModule a = includedModules[j];
                    tmpAeroModules.Add(a);
                }

                includedModules.Clear();
                weighting.Clear();

            }
            foreach(KeyValuePair<FARAeroPartModule, FARAeroPartModule.ProjectedArea> pair in _moduleAndAreas)
            {
                pair.Key.SetProjectedArea(pair.Value, _localToWorldMatrix);
            }
            _newAeroModules = tmpAeroModules.ToList();

            _newUnusedAeroModules = new List<FARAeroPartModule>();

            for (int i = 0; i < _currentGeoModules.Count; i++)
            {
                FARAeroPartModule aeroModule = _currentGeoModules[i].GetComponent<FARAeroPartModule>();
                if (aeroModule != null && !tmpAeroModules.Contains(aeroModule))
                    _newUnusedAeroModules.Add(aeroModule);
            }

            ferram4.FARCenterQuery center = new ferram4.FARCenterQuery();

            Vector3 worldMainAxis = _localToWorldMatrix.MultiplyVector(_vehicleMainAxis);
            worldMainAxis.Normalize();

            for (int i = 0; i < _newAeroSections.Count; i++)
            {
                FARAeroSection a = _newAeroSections[i];
                a.PredictionCalculateAeroForces(2f, 1f, 50000f, 0.005f, worldMainAxis, center);
            }

            _sonicDragArea = Vector3.Dot(center.force, worldMainAxis) * -1000;
        }

        private double CalculateHypersonicMoment(double lowArea, double highArea, double sectionThickness)
        {
            if(lowArea >= highArea)
                return 0;

            double r1, r2;
            r1 = Math.Sqrt(lowArea / Math.PI);
            r2 = Math.Sqrt(highArea / Math.PI);

            double moment = r2 * r2 + r1 * r1 + sectionThickness * sectionThickness * 0.5;
            moment *= 2 * Math.PI;

            double radDiffSq = (r2 - r1);
            radDiffSq *= radDiffSq;

            moment *= radDiffSq;
            moment /= sectionThickness * sectionThickness + radDiffSq;

            return -moment * sectionThickness;
        }

        private double CalculateHypersonicDrag(double lowArea, double highArea, double sectionThickness)
        {
            if (lowArea >= highArea)
                return 0;

            double r1, r2;
            r1 = Math.Sqrt(lowArea / Math.PI);
            r2 = Math.Sqrt(highArea / Math.PI);

            double radDiff = r2 - r1;
            double radDiffSq = radDiff * radDiff;

            double drag = sectionThickness * sectionThickness + radDiffSq;
            drag = 2d * Math.PI / drag;
            drag *= radDiff * radDiffSq * (r1 + r2);

            return -drag;        //force is negative 
        }

        private double CalcAllTransonicWaveDrag(VoxelCrossSection[] sections, int front, int numSections, double sectionThickness)
        {
            double drag = 0;
            double Lj;

            for(int j = 0; j < numSections; j++)
            {
                double accumulator = 0;

                Lj = (j + 0.5) * Math.Log(j + 0.5);
                if (j > 0)
                    Lj -= (j - 0.5) * Math.Log(j - 0.5);

                for(int i = j; i < numSections; i++)
                {
                    accumulator += sections[front + i].secondAreaDeriv * sections[front + i - j].secondAreaDeriv;
                }

                drag += accumulator * Lj;
            }

            drag *= -sectionThickness * sectionThickness / Math.PI;
            return drag;
        }

        private double CalculateTransonicWaveDrag(int i, int index, int numSections, int front, double sectionThickness, double cutoff)
        {
            double currentSectAreaCrossSection = MathClampAbs(_vehicleCrossSection[index].secondAreaDeriv, cutoff);

            if (currentSectAreaCrossSection == 0)       //quick escape for 0 cross-section section drag
                return 0;

            double lj2ndTerm = 0;
            double drag = 0;
            int limDoubleDrag = Math.Min(i, numSections - i);
            double sectionThicknessSq = sectionThickness * sectionThickness;

            double lj3rdTerm = Math.Log(sectionThickness) - 1;

            lj2ndTerm = 0.5 * Math.Log(0.5);
            drag = currentSectAreaCrossSection * (lj2ndTerm + lj3rdTerm);


            for (int j = 1; j <= limDoubleDrag; j++)      //section of influence from ahead and behind
            {
                double thisLj = (j + 0.5) * Math.Log(j + 0.5);
                double tmp = thisLj;
                thisLj -= lj2ndTerm;
                lj2ndTerm = tmp;

                tmp = MathClampAbs(_vehicleCrossSection[index + j].secondAreaDeriv, cutoff);
                tmp += MathClampAbs(_vehicleCrossSection[index - j].secondAreaDeriv, cutoff);

                thisLj += lj3rdTerm;

                drag += tmp * thisLj;
            }
            if (i < numSections - i)
            {
                for (int j = 2 * i + 1; j < numSections; j++)
                {
                    double thisLj = (j - i + 0.5) * Math.Log(j - i + 0.5);
                    double tmp = thisLj;
                    thisLj -= lj2ndTerm;
                    lj2ndTerm = tmp;

                    tmp = MathClampAbs(_vehicleCrossSection[j + front].secondAreaDeriv, cutoff);

                    thisLj += lj3rdTerm;

                    drag += tmp * thisLj;
                }
            }
            else if (i > numSections - i)
            {
                for (int j = numSections - 2 * i - 2; j >= 0; j--)
                {
                    double thisLj = (i - j + 0.5) * Math.Log(i - j + 0.5);
                    double tmp = thisLj;
                    thisLj -= lj2ndTerm;
                    lj2ndTerm = tmp;

                    tmp = MathClampAbs(_vehicleCrossSection[j + front].secondAreaDeriv, cutoff);

                    thisLj += lj3rdTerm;

                    drag += tmp * thisLj;
                }
            }

            drag *= sectionThicknessSq;
            drag /= 2 * Math.PI;
            drag *= currentSectAreaCrossSection;
            return drag;
        }

        private double CalculateCriticalMachNumber(double finenessRatio)
        {
            if (finenessRatio > 10)
                return 0.925;
            if (finenessRatio < 1.5)
                return 0.285;
            if (finenessRatio > 4)
            {
                if (finenessRatio > 6)
                    return 0.00625 * finenessRatio + 0.8625;

                return 0.025 * finenessRatio + 0.75;
            }
            else if (finenessRatio < 3)
                return 0.33 * finenessRatio - 0.21;

            return 0.07 * finenessRatio + 0.57;
        }

        private double CriticalMachFactorForUnsmoothCrossSection(VoxelCrossSection[] crossSections, double finenessRatio, double sectionThickness)
        {
            double maxAbsRateOfChange = 0;
            double prevArea = 0;
            double invSectionThickness = 1/ sectionThickness;

            for(int i = 0; i < crossSections.Length; i++)
            {
                double currentArea = crossSections[i].area;
                double absRateOfChange = Math.Abs(currentArea - prevArea) * invSectionThickness;
                if (absRateOfChange > maxAbsRateOfChange)
                    maxAbsRateOfChange = absRateOfChange;
                prevArea = currentArea;
            }

            //double normalizedRateOfChange = maxAbsRateOfChange / _maxCrossSectionArea;

            double maxCritMachAdjustmentFactor = 2 * _maxCrossSectionArea + 5 * maxAbsRateOfChange;
            maxCritMachAdjustmentFactor = 0.5 + _maxCrossSectionArea / maxCritMachAdjustmentFactor;     //will vary based on x = maxAbsRateOfChange / _maxCrossSectionArea from 1 @ x = 0 to 0.5 as x -> infinity

            double critAdjustmentFactor = 4 + finenessRatio;
            critAdjustmentFactor = 6 * (1 - maxCritMachAdjustmentFactor) / critAdjustmentFactor;
            critAdjustmentFactor += maxCritMachAdjustmentFactor;

            if (critAdjustmentFactor > 1)
                critAdjustmentFactor = 1;

            return critAdjustmentFactor;
        }
        #endregion

        double MathClampAbs(double value, double abs)
        {
            if (value < -abs)
                return -abs;
            if (value > abs)
                return abs;
            return value;
        }

        struct CrossSectionAdjustData
        {
            public double activeAreaRemoved;
            public int lastIndex;
            public int counter;

            public CrossSectionAdjustData(double activeAreaRemoved, int lastIndex)
            {
                this.activeAreaRemoved = activeAreaRemoved;
                this.lastIndex = lastIndex;
                counter = 0;
            }
        }
    }
}
