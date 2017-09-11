﻿//
// BIMRL (BIM Rule Language) Simplified Schema ETL (Extract, Transform, Load) library: this library transforms IFC data into BIMRL Simplified Schema for RDBMS. 
// This work is part of the original author's Ph.D. thesis work on the automated rule checking in Georgia Institute of Technology
// Copyright (C) 2013 Wawan Solihin (borobudurws@hotmail.com)
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 3 of the License, or any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.IO;
using System.Threading.Tasks;
#if ORACLE
using Oracle.DataAccess.Client;
using NetSdoGeometry;
#endif
#if POSTGRES
using Npgsql;
using NpgsqlTypes;
#endif
using BIMRL.Common;
using Newtonsoft.Json;
using Ionic.Zip;

namespace BIMRL
{
   public class BIMRLDiffModels
   {
      int? compNewModel;
      int? compRefModel;
      BIMRLCommon m_BIMRLCommonRef;

      //string scopeNewTable = "diffscopenew";
      //string scopeRefTable = "diffscoperef";
      string scopeTable = "diffscope";
      bool hasScope = false;

      public string ScopeElement { get; set; } = null;
      public string ScopeCondition { get; set; } = null;
      public bool UseProperty { get; set; } = false;
      public BoundingBox3D ScopeGeom { get; set; } = null;

      public string GraphicsZipFile { get; private set; }
      IDictionary<string, DataTable> diffResultsDict = new Dictionary<string, DataTable>();
#if ORACLE
      string diffKeyword = " minus ";
      bool doCommit = false;
# endif
#if POSTGRES
      string diffKeyword = " except ";
      bool doCommit = true;
#endif

      public BIMRLDiffModels(int modelId1, int modelId2, BIMRLCommon bimrlCommon)
      {
         compNewModel = modelId1;
         compRefModel = modelId2;
         m_BIMRLCommonRef = bimrlCommon;
      }

      public BIMRLDiffModels(BIMRLCommon bimrlCommon)
      {
         compNewModel = null;
         compRefModel = null;
         m_BIMRLCommonRef = bimrlCommon;
      }

      public void RunDiff(string outputFileName, int? newModel = null, int? refModel = null, BIMRLDiffOptions options = null)
      {
         if (options == null)
            options = BIMRLDiffOptions.SelectAllOptions();

         if (newModel.HasValue)
            compNewModel = newModel.Value;
         if (refModel.HasValue)
            compRefModel = refModel.Value;

         if ((!compNewModel.HasValue || !compRefModel.HasValue) && (!newModel.HasValue || !refModel.HasValue))
            throw new Exception("Model IDs must be supplied!");

         DefineScope();
        
         string elemTableNew = DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value);
         string elemTableRef = DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value);

         DBOperation.beginTransaction();
         // Create tables to keep track of the new or deleted objects
         runNonQuery("drop table newelements", true, doCommit);
         runNonQuery("create table newelements as select elementid from " + elemTableNew + diffKeyword + "select elementid from " + elemTableRef, true, doCommit);
         runNonQuery("drop table deletedelements", true, doCommit);
         runNonQuery("create table deletedelements as select elementid from " + elemTableRef + diffKeyword + "select elementid from " + elemTableNew, true, doCommit);

         if (options.CheckNewAndDeletedObjects)
            AddResultDict("CheckNewAndDeletedObjects", DiffObjects());

         if (options.CheckGeometriesDiffBySignature)
         {
            GraphicsZipFile = Path.Combine(Path.GetDirectoryName(outputFileName), Path.GetFileNameWithoutExtension(outputFileName) + "-GraphicsOutput.zip");
            AddResultDict("CheckGeometriesDiff", DiffGeometry(GraphicsZipFile, options.GeometryCompareTolerance));
         }

         if (options.CheckTypeAndTypeAssignments)
            AddResultDict("CheckTypeAndTypeAssignments", DiffType());

         if (options.CheckContainmentRelationships)
            AddResultDict("CheckContainmentRelationships", DiffContainment());

         if (options.CheckOwnerHistory)
            AddResultDict("CheckOwnerHistory", DiffOwnerHistory());

         if (options.CheckProperties)
            AddResultDict("CheckProperties", DiffProperty());

         if (options.CheckMaterials)
            AddResultDict("CheckMaterials", DiffMaterial());

         if (options.CheckClassificationAssignments)
            AddResultDict("CheckClassificationAssignments", DiffClassification());

         if (options.CheckGroupMemberships)
            AddResultDict("CheckGroupMemberships", DiffGroupMembership());

         if (options.CheckAggregations)
            AddResultDict("CheckAggregations", DiffAggregation());

         if (options.CheckConnections)
            AddResultDict("CheckConnections", DiffConnection());

         if (options.CheckElementDependencies)
            AddResultDict("CheckElementDependencies", DiffElementDependency());

         if (options.CheckSpaceBoundaries)
            AddResultDict("CheckSpaceBoundaries", DiffSpaceBoundary());

         try
         {
            string json = JsonConvert.SerializeObject(diffResultsDict, Formatting.Indented);
            using (StreamWriter file = File.CreateText(outputFileName))
            {
               JsonSerializer serializer = new JsonSerializer();
               serializer.Serialize(file, diffResultsDict);
            }
         }
         catch
         {
            throw new Exception("Fail to sereialize to " + outputFileName);
         }
      }

      IList<DataTable> DiffObjects()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string elemTableNew = DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value);
         string elemTableRef = DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value);

         string whereNew = "";
         if (hasScope)
            whereNew = " where elementid in (select elementid from newelements intersect select elementid from " + scopeTable + ")";
         else
            whereNew = " where elementid in (select elementid from newelements)";
         string newElementReport = "select elementid, elementtype, name, longname, description, modelid, container, typeid from " + elemTableNew 
                                    + whereNew;

         DataTable newElemRes = queryMultipleRows(newElementReport, "New Elements");
         qResults.Add(newElemRes);

         string whereDel = "";
         if (hasScope)
            whereDel = " where elementid in (select elementid from deletedelements intersect select elementid from " + scopeTable + ")";
         else
            whereDel = " where elementid in (select elementid from deletedelements)";

         string delElementReport = "select elementid, elementtype, name, longname, description, modelid, container, typeid from " + elemTableRef
                                    + whereDel;

         DataTable delElemRes = queryMultipleRows(delElementReport, "Deleted Elements");
         qResults.Add(delElemRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffGeometry(string graphicsOutputZip, double tol = 0.0001)
      {
         IList<DataTable> qResults = new List<DataTable>();
         string elemTableNew = DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value);
         string elemTableRef = DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value);
         double tolNeg = -tol;

         string scopeConditionJoin = "";
         if (hasScope)
            scopeConditionJoin = " and a.elementid in (select elementid from " + scopeTable + ") and b.elementid in (select elementid from " + scopeTable + ")";

         DBOperation.beginTransaction();
         string diffGeomReport = "select a.elementid \"Elementid\", a.elementtype \"Element Type\", a.total_surface_area \"Surface Area (New)\", b.total_surface_area \"Surface Area (Ref)\","
                                    + " a.geometrybody_bbox_centroid \"Centroid (New)\", b.geometrybody_bbox_centroid \"Centroid (Ref)\", a.geometrybody_bbox \"Bounding Box (New)\", b.geometrybody_bbox \"Bounding Box (Ref)\""
                                    + " from " + elemTableNew + " a, " + elemTableRef + " b"
                                    + " where a.elementid = b.elementid and a.geometrybody is not null"
                                    + " and ((a.total_surface_area - b.total_surface_area) < " + tolNeg.ToString("G") + " or (a.total_surface_area - b.total_surface_area) > " + tol.ToString("G")
#if ORACLE
                                    + " or sdo_geom.sdo_difference(a.geometrybody_bbox, b.geometrybody_bbox, " + tol.ToString("G") + ") is not null"
                                    + " or sdo_geom.sdo_difference(a.geometrybody_bbox_centroid, b.geometrybody_bbox_centroid, " + tol.ToString("G") + ") is not null)"
#endif
#if POSTGRES
                                    + " or boxequal(a.geometrybody_bbox[1], a.geometrybody_bbox[2], b.geometrybody_bbox[1], b.geometrybody_bbox[2], " + tol.ToString("G") + ")=false"
                                    + " or distance(a.geometrybody_bbox_centroid, b.geometrybody_bbox_centroid) > " + tol.ToString("G") + ")"
#endif
                                    + scopeConditionJoin;

         DataTable geomDiffRes = queryMultipleRows(diffGeomReport, "Geometry Difference By Signature");

         // Go through the differences and show them in X3d file (need to add the X3d file name into the geomDiffRes
         exportGraphicsDiff(geomDiffRes, compNewModel.Value, compRefModel.Value, graphicsOutputZip);

         qResults.Add(geomDiffRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      void exportGraphicsDiff(DataTable geomDiffRes, int modelIDNew, int modelIDRef, string zippedX3DFiles, bool createSummary=true)
      {
         string tempDirectory = Path.Combine(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()));
         Directory.CreateDirectory(tempDirectory);

         DBOperation.beginTransaction();
         geomDiffRes.Columns.Add("GraphicsFile");

         if (createSummary)
         {
            string x3dFile = "DiffSummary.x3d";
            BIMRLExportSDOToX3D x3dExp = new BIMRLExportSDOToX3D(m_BIMRLCommonRef, Path.Combine(tempDirectory, x3dFile));    // Initialize first

            string condition = "";
            if (geomDiffRes.Rows.Count > 1000)
            {
               // Oracle has a limitation that expression can only have 1000 items. Use a temporary table to do this
               DBOperation.beginTransaction();
#if ORACLE
               DBOperation.executeSingleStmt("Drop table DiffResTemp", commit: true);
               DBOperation.executeSingleStmt("Create global temporary table DiffResTemp (Elementid varchar(48), NewOrOld integer)", commit: true);
#endif
#if POSTGRES
               DBOperation.executeSingleStmt("Create temporary table DiffResTemp (Elementid varchar(48)) on commit drop", commit: false);
#endif
               foreach (DataRow row in geomDiffRes.Rows)
               {
                  string elemid = row["Elementid"].ToString();
                  if (row["Bounding Box (New)"] != null)
                  {
                     x3dExp.IDsToHighlight.Add(elemid);
                     x3dExp.highlightColor = new ColorSpec();
                     x3dExp.highlightColor.emissiveColorRed = 0;      // Set the New object to GREEN color
                     x3dExp.highlightColor.emissiveColorGreen = 255;
                     x3dExp.highlightColor.emissiveColorBlue = 0;
                     x3dExp.transparencyOverride = 0.30;
                     DBOperation.insertRow("insert into DiffResTemp (Elementid) values ('" + elemid + ")");
                  }
               }
               condition = "elementid in (select elementid from DiffResTemp)";
               x3dExp.exportElemGeomToX3D(modelIDNew, condition);
               DBOperation.executeSingleStmt("Truncate table DiffResTemp");

               foreach (DataRow row in geomDiffRes.Rows)
               {
                  string elemid = row["Elementid"].ToString();
                  if (row["Bounding Box (Ref)"] != null)
                  {
                     x3dExp.IDsToHighlight.Add(elemid);
                     x3dExp.highlightColor.emissiveColorRed = 255;      // Set the New object to RED color
                     x3dExp.highlightColor.emissiveColorGreen = 0;
                     x3dExp.highlightColor.emissiveColorBlue = 0;
                     x3dExp.transparencyOverride = 0.30;
                     DBOperation.insertRow("insert into DiffResTemp (Elementid) values ('" + elemid + ")");
                  }
               }
               condition = "elementid in (select elementid from DiffResTemp)";
               x3dExp.exportElemGeomToX3D(modelIDRef, condition);
               DBOperation.commitTransaction();
            }
            else
            {
               condition = "";
               foreach (DataRow row in geomDiffRes.Rows)
               {
                  string elemid = row["Elementid"].ToString();
                  if (row["Bounding Box (New)"] != null)
                  {
                     x3dExp.IDsToHighlight.Add(elemid);
                     x3dExp.highlightColor = new ColorSpec();
                     x3dExp.highlightColor.emissiveColorRed = 0;      // Set the New object to GREEN color
                     x3dExp.highlightColor.emissiveColorGreen = 255;
                     x3dExp.highlightColor.emissiveColorBlue = 0;
                     x3dExp.transparencyOverride = 0.30;
                     BIMRLCommon.appendToString("'" + elemid + "'", ",", ref condition);
                  }
               }
               condition = "elementid in (" + condition + ")";
               x3dExp.exportElemGeomToX3D(modelIDNew, condition);

               condition = "";
               foreach (DataRow row in geomDiffRes.Rows)
               {
                  string elemid = row["Elementid"].ToString();
                  if (row["Bounding Box (Ref)"] != null)
                  {
                     x3dExp.IDsToHighlight.Add(elemid);
                     x3dExp.highlightColor.emissiveColorRed = 255;      // Set the New object to RED color
                     x3dExp.highlightColor.emissiveColorGreen = 0;
                     x3dExp.highlightColor.emissiveColorBlue = 0;
                     x3dExp.transparencyOverride = 0.30;
                     BIMRLCommon.appendToString("'" + elemid + "'", ",", ref condition);
                  }
               }
               condition = "elementid in (" + condition + ")";
               x3dExp.exportElemGeomToX3D(modelIDRef, condition);
            }

            // Add background element from IFCSLAB to give a sense of spatial relative location
            x3dExp.transparencyOverride = 0.8;
            string whereCondElemGeom = "ELEMENTID IN (SELECT ELEMENTID FROM " + DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value)
                                       + " WHERE upper(elementtype) like 'IFCSLAB%' or upper(elementtype) in ('OST_FLOORS','OST_ROOFS'))";
            x3dExp.exportElemGeomToX3D(modelIDNew, whereCondElemGeom);

            // Draw the scope box
            if (ScopeGeom != null)
               x3dExp.drawBBToX3D(ScopeGeom);

            x3dExp.endExportToX3D();

            DBOperation.commitTransaction();
         }

         foreach (DataRow row in geomDiffRes.Rows)
         {
            string elemid = row["Elementid"].ToString();
            string x3dFile = elemid + ".x3d";
            BIMRLExportSDOToX3D x3dExp = new BIMRLExportSDOToX3D(m_BIMRLCommonRef, Path.Combine(tempDirectory, x3dFile));    // Initialize first

            if (row["Bounding Box (New)"] != null)
            {
               x3dExp.IDsToHighlight.Add(elemid);
               x3dExp.highlightColor = new ColorSpec();
               x3dExp.highlightColor.emissiveColorRed = 0;      // Set the New object to GREEN color
               x3dExp.highlightColor.emissiveColorGreen = 255;
               x3dExp.highlightColor.emissiveColorBlue = 0;
               x3dExp.transparencyOverride = 0.30;
               x3dExp.exportElemGeomToX3D(modelIDNew, "elementid='" + elemid + "'");
            }

            if (row["Bounding Box (Ref)"] != null)
            {
               x3dExp.IDsToHighlight.Add(elemid);
               x3dExp.highlightColor.emissiveColorRed = 255;      // Set the New object to RED color
               x3dExp.highlightColor.emissiveColorGreen = 0;
               x3dExp.highlightColor.emissiveColorBlue = 0;
               x3dExp.transparencyOverride = 0.30;
               x3dExp.exportElemGeomToX3D(modelIDRef, "elementid='" + elemid + "'");
            }

            // Add background element from IFCSLAB to give a sense of spatial relative location
            x3dExp.transparencyOverride = 0.8;
            string whereCondElemGeom = "ELEMENTID IN (SELECT ELEMENTID FROM " + DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value) 
                                       + " WHERE upper(elementtype) like 'IFCSLAB%' or upper(elementtype) in ('OST_FLOORS','OST_ROOFS'))";
            x3dExp.exportElemGeomToX3D(modelIDNew, whereCondElemGeom);
            
            // Draw the scope box
            if (ScopeGeom != null)
               x3dExp.drawBBToX3D(ScopeGeom);

            x3dExp.endExportToX3D();
            row["GraphicsFile"] = x3dFile;
         }

         if (File.Exists(zippedX3DFiles))
            File.Delete(zippedX3DFiles);
         try
         {
            using (ZipFile zip = new ZipFile(zippedX3DFiles))
            {
               zip.AddDirectory(tempDirectory);
               zip.Save();
               Directory.Delete(tempDirectory, true);
            }
         }
         catch (Exception e)
         {
            DBOperation.rollbackTransaction();
            throw new Exception("Error during writing the zip file! " + e.Message);
         }
         DBOperation.rollbackTransaction();
      }

      IList<DataTable> DiffType()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string elemTableNew = DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value);
         string elemTableRef = DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value);
         string typeTableNew = DBOperation.formatTabName("BIMRL_TYPE", compNewModel.Value);
         string typeTableRef = DBOperation.formatTabName("BIMRL_TYPE", compRefModel.Value);

         //string newTypeReport = "select elementid, ifctype, name from " + typeTableNew + diffKeyword
         //                        + "select elementid, ifctype, name from " + typeTableRef;

         //DataTable newTypeRes = queryMultipleRows(newTypeReport);
         //newTypeRes.TableName = "New Types";
         //qResults.Add(newTypeRes);

         //string delTypeReport = "select elementid, ifctype, name from " + typeTableRef + diffKeyword
         //               + "select elementid, ifctype, name from " + typeTableNew;

         //DataTable delTypeRes = queryMultipleRows(delTypeReport);
         //delTypeRes.TableName = "Deleted Types";
         //qResults.Add(delTypeRes);
         string scopeConditionJoin = "";
         if (hasScope)
            scopeConditionJoin = " and tab1.id_new in (select elementid from " + scopeTable + ") and tab2.id_ref in (select elementid from " + scopeTable + ")";

         DBOperation.beginTransaction();
         string TypeAssignmentReport = "select tab1.*, tab2.* from "
                                       + "(select a.elementid id_new, a.elementtype \"Element Type (New)\", a.name \"Element Name (New)\", b.ifcType \"IFC Type Entity (New)\", b.elementid as typeid_new, b.name as typename_new from " + elemTableNew + " a, " + typeTableNew + " b where b.elementid = a.typeid) tab1 "
                                       + "full outer join "
                                       + "(select c.elementid as id_ref, c.elementtype \"Element Type (Ref)\", c.name \"Element Name (Ref)\", d.ifcType \"IFC Type Entity (Ref)\", d.elementid as typeid_ref, d.name as typename_ref from " + elemTableRef + " c, " + typeTableRef + " d where d.elementid = c.typeid) tab2 "
                                       + "on tab1.id_new = tab2.id_ref "
                                       + "where tab1.typename_new != tab2.typename_ref or (tab1.typename_new is not null and tab2.typename_ref is null) or (tab1.typename_new is null and tab2.typename_ref is not null)"
                                       + scopeConditionJoin;

         DataTable assigElemRes = queryMultipleRows(TypeAssignmentReport, "Type Assignment Changes");
         qResults.Add(assigElemRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }


      IList<DataTable> DiffContainment()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string elemTableNew = DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value);
         string elemTableRef = DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value);
         string scopeConditionJoin = "";
         if (hasScope)
            scopeConditionJoin = " and tab1.id_new in (select elementid from " + scopeTable + ") and tab2.id_ref in (select elementid from " + scopeTable + ")";

         DBOperation.beginTransaction();
         string containerReport = "select tab1.*, tab2.* from "
                                 + "(select a.elementid id_new, a.elementtype \"Element Type (New)\", a.name \"Element Name (New)\", b.elementid as containerid_new, b.name as containername_new, b.longname as containerlongname_new from " + elemTableNew + " a, " + elemTableNew + " b where b.elementid = a.container) tab1 "
                                 + "full outer join "
                                 + "(select c.elementid as id_ref, c.elementtype \"Element Type (Ref)\", c.name \"Element Name (Ref)\", d.elementid as containerid_ref, d.name as containername_ref, d.longname as containerlongname_ref from " + elemTableRef + " c, " + elemTableRef + " d where d.elementid = c.container) tab2 "
                                 + "on tab1.id_new = tab2.id_ref "
                                 + "where tab1.containerid_new != tab2.containerid_ref or(tab1.containerid_new is not null and tab2.containerid_ref is null) or(tab1.containerid_new is null and tab2.containerid_ref is not null)"
                                 + scopeConditionJoin;

         DataTable containerRes = queryMultipleRows(containerReport, "Container Assignment Changes");
         qResults.Add(containerRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffOwnerHistory()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string oHTableNew = DBOperation.formatTabName("BIMRL_OWNERHISTORY", compNewModel.Value);
         string oHTableRef = DBOperation.formatTabName("BIMRL_OWNERHISTORY", compRefModel.Value);

         DBOperation.beginTransaction();
         string oHReport = "select a.*, b.* from " + oHTableNew + " a "
                                 + "full outer join " + oHTableRef + " b "
                                 + "on ( a.id = b.id and a.modelid = b.modelid) ";
         DataTable oHRes = queryMultipleRows(oHReport, "Owner History Changes");
         qResults.Add(oHRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffProperty()
      {
         IList<DataTable> qResults = new List<DataTable>();

         DBOperation.beginTransaction();
         string propTableNew = DBOperation.formatTabName("BIMRL_PROPERTIES", compNewModel.Value);
         string propTableRef = DBOperation.formatTabName("BIMRL_PROPERTIES", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " where elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " where elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.elementid in (select elementid from " + scopeTable + ") and b.elementid in (select elementid from " + scopeTable + ")";
         }

         string elemNewProps = "select elementid, fromtype, propertygroupname, propertyname from " + propTableNew
                                + scopeConditionNew
                                + diffKeyword
                                + "select elementid, fromtype, propertygroupname, propertyname from " + propTableRef
                                + scopeConditionDel
                                + " order by elementid, fromtype, propertygroupname, propertyname";

         DataTable elemNewRes = queryMultipleRows(elemNewProps, "Elements with New Properties");
         qResults.Add(elemNewRes);

         string elemDelProps = "select elementid, fromtype, propertygroupname, propertyname from " + propTableRef
                                + scopeConditionDel
                                + diffKeyword
                                + "select elementid, fromtype, propertygroupname, propertyname from " + propTableNew
                                + scopeConditionNew
                                + " order by elementid, fromtype, propertygroupname, propertyname";

         DataTable elemDelRes = queryMultipleRows(elemDelProps, "Elements with Deleted Properties");
         qResults.Add(elemDelRes);

         string changeProps = "select a.elementid \"Element ID (New)\", a.fromType \"Type?\", a.propertygroupname \"PSet Name (New)\", a.propertyname \"Property Name (New)\", a.propertyvalue \"Value (New)\", " 
                              + "b.propertyvalue \"Value (Ref)\", b.propertyname \"Property Name (Ref)\", b.propertygroupname \"PSet Name (Ref)\", b.fromType \"Type?\", b.elementid \"Deleted ID\"  from " + propTableNew + " a "
                              + "full outer join " + propTableRef 
                              + " b on (a.elementid = b.elementid and a.propertygroupname = b.propertygroupname and a.propertyname = b.propertyname) "
                              + "where (a.propertyvalue != b.propertyvalue or(a.propertyvalue is null and b.propertyvalue is not null) or(a.propertyvalue is not null and b.propertyvalue is null)) "
                              + "and (a.elementid not in (select elementid from newelements) or b.elementid not in (select elementid from deletedelements))"
                              + scopeConditionJoin;

         DataTable changePropRes = queryMultipleRows(changeProps, "Elements with Chages in Property Value");
         qResults.Add(changePropRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffMaterial()
      {
         IList<DataTable> qResults = new List<DataTable>();

         DBOperation.beginTransaction();
         string matTableNew = DBOperation.formatTabName("BIMRL_ELEMENTMATERIAL", compNewModel.Value);
         string matTableRef = DBOperation.formatTabName("BIMRL_ELEMENTMATERIAL", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.elementid in (select elementid from " + scopeTable + ") and b.elementid in (select elementid from " + scopeTable + ")";
         }

         string newMatReport = "select elementid, materialname, materialthickness from " + matTableNew + " where setname is null "
                                 + scopeConditionNew
                                 + diffKeyword
                                 + "select elementid, materialname, materialthickness from " + matTableRef + " where setname is null"
                                 + scopeConditionDel;

         DataTable newMatRes = queryMultipleRows(newMatReport, "New Material (Single/List)");
         qResults.Add(newMatRes);

         string delMatReport = "select elementid, materialname, materialthickness from " + matTableRef + " where setname is null "
                        + scopeConditionNew
                        + diffKeyword
                        + "select elementid, materialname, materialthickness from " + matTableNew + " where setname is null"
                        + scopeConditionDel;

         DataTable delMatRes = queryMultipleRows(delMatReport, "Deleted Material (Single/List)");
         qResults.Add(delMatRes);

         string newMatSetReport = "select a.elementid, a.setname, a.materialname \"New Material\", b.materialname \"Ref Material\" from " + matTableNew + " a "
                                 + "left outer join " + matTableRef + " b on (a.elementid = b.elementid and a.setname=b.setname) "
                                 + "where a.setname is not null and b.setname is not null and ((a.materialname != b.materialname) "
                                 + "or (a.materialname is null and b.materialname is not null) or (a.materialname is not null and b.materialname is null))"
                                 + scopeConditionJoin; 

         DataTable newMatSetRes = queryMultipleRows(newMatSetReport, "New Material Set");
         qResults.Add(newMatSetRes);

         string delMatSetReport = "select a.elementid, a.setname, a.materialname \"Deleted Material\" from " + matTableRef + " a "
                                 + "left outer join " + matTableNew + " b on (a.elementid = b.elementid and a.setname=b.setname) "
                                 + "where a.setname is not null and b.setname is not null and ((a.materialname != b.materialname) "
                                 + "or (a.materialname is null and b.materialname is not null) or (a.materialname is not null and b.materialname is null))"
                                 + scopeConditionJoin;

         DataTable delMatSetRes = queryMultipleRows(delMatSetReport, "Deleted Material Set");
         qResults.Add(delMatSetRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffClassification()
      {
         IList<DataTable> qResults = new List<DataTable>();

         DBOperation.beginTransaction();
         string classifTableNew = DBOperation.formatTabName("BIMRL_CLASSIFASSIGNMENT", compNewModel.Value);
         string classifTableRef = DBOperation.formatTabName("BIMRL_CLASSIFASSIGNMENT", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.elementid in (select elementid from " + scopeTable + ") and b.elementid in (select elementid from " + scopeTable + ")";
         }

         string classifReport = "select a.elementid \"ElementID (New)\", a.classificationname \"Classification Name (New)\", a.classificationitemcode \"Code (New)\", a.fromtype \"FromType?\", "
                                 + "b.elementid \"ElementID (Ref)\", b.classificationname \"Classification Name (Ref)\", b.classificationitemcode \"Code (Ref)\", b.fromtype \"FromType?\""
                                 + " from " + classifTableNew + " a full outer join " + classifTableRef + " b on (a.elementid = b.elementid and a.classificationname = b.classificationname and a.classificationitemcode = b.classificationitemcode) "
                                 + "where (a.classificationitemcode is null and b.classificationitemcode is not null) or (a.classificationitemcode is not null and b.classificationitemcode is null)"
                                 + scopeConditionJoin;

         DataTable classifRes = queryMultipleRows(classifReport, "Classification Assignments");
         qResults.Add(classifRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffGroupMembership()
      {
         IList<DataTable> qResults = new List<DataTable>();

         DBOperation.beginTransaction();
         string groupRelTableNew = DBOperation.formatTabName("BIMRL_RELGROUP", compNewModel.Value);
         string groupRelTableRef = DBOperation.formatTabName("BIMRL_RELGROUP", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.memberelementid in (select elementid from " + scopeTable + ") and b.memberelementid in (select elementid from " + scopeTable + ")";
         }

         string groupRelReport = "Select a.groupelementid \"Group ID (New)\", a.groupelementtype \"Group Type (New)\", a.memberelementid \"Member ID (New)\", a.memberelementtype \"Member Type (New)\", "
                                 + "b.groupelementid \"Group ID (Ref)\", b.groupelementtype \"Group Type (Ref)\", b.memberelementid \"Member ID (Ref)\", b.memberelementtype \"Member Type (Ref)\" "
                                 + "from " + groupRelTableNew + " a full outer join " + groupRelTableRef + " b on (a.groupelementid=b.groupelementid and a.memberelementid=b.memberelementid) "
                                 + "where (a.memberelementid is null and b.memberelementid is not null) or (a.memberelementid is not null and b.memberelementid is null)"
                                 + scopeConditionJoin;

         DataTable groupRelRes = queryMultipleRows(groupRelReport, "Group Membership");
         qResults.Add(groupRelRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }


      IList<DataTable> DiffAggregation()
      {
         IList<DataTable> qResults = new List<DataTable>();

         DBOperation.beginTransaction();
         string aggrTableNew = DBOperation.formatTabName("BIMRL_RELAGGREGATION", compNewModel.Value);
         string aggrTableRef = DBOperation.formatTabName("BIMRL_RELAGGREGATION", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.aggregateelementid in (select elementid from " + scopeTable + ") and b.aggregateelementid in (select elementid from " + scopeTable + ")";
         }

         string aggrReport = "Select a.masterelementid \"Master ID (New)\", a.masterelementtype \"Master Type (New)\", a.aggregateelementid \"Aggre ID (New)\", a.aggregateelementtype \"Aggr Type (New)\", "
                              + "b.masterelementid \"Master ID (Ref)\", b.masterelementtype \"Master Type (Ref)\", b.aggregateelementid \"Aggre ID (Ref)\", b.aggregateelementtype \"Aggr Type (Ref)\" "
                              + "from " + aggrTableNew + " a full outer join " + aggrTableRef + " b on (a.masterelementid=b.masterelementid and a.aggregateelementid=b.aggregateelementid) "
                              + "where (a.aggregateelementid is null and b.aggregateelementid is not null) or (a.aggregateelementid is not null and b.aggregateelementid is null)"
                              + scopeConditionJoin;

         DataTable aggrRes = queryMultipleRows(aggrReport, "Aggregation Changes");
         qResults.Add(aggrRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffConnection()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string connTableNew = DBOperation.formatTabName("BIMRL_RELCONNECTION", compNewModel.Value);
         string connTableRef = DBOperation.formatTabName("BIMRL_RELCONNECTION", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.CONNECTINGELEMENTID in (select elementid from " + scopeTable + ") and b.CONNECTINGELEMENTID in (select elementid from " + scopeTable + ")";
         }

         DBOperation.beginTransaction();
         string connReport = "Select a.CONNECTINGELEMENTID \"Connecting Elem ID (New)\", a.CONNECTINGELEMENTTYPE \"Connecting Type (New)\", a.CONNECTEDELEMENTID \"Connected Elem ID(New)\", a.CONNECTEDELEMENTTYPE \"Connected Type (New)\", "
                              + "b.CONNECTINGELEMENTID \"Connecting Elem ID (Ref)\", b.CONNECTINGELEMENTTYPE \"Connecting Type (Ref)\", b.CONNECTEDELEMENTID \"Connected Elem ID(Ref)\", b.CONNECTEDELEMENTTYPE \"Connected Type (Ref)\" "
                              + "from " + connTableNew + " a full outer join " + connTableRef + " b on (a.CONNECTINGELEMENTID=b.CONNECTINGELEMENTID and a.CONNECTEDELEMENTID=b.CONNECTEDELEMENTID) "
                              + "where (a.CONNECTINGELEMENTID is null and b.CONNECTINGELEMENTID is not null) or (a.CONNECTINGELEMENTID is not null and b.CONNECTINGELEMENTID is null) "
                              + "or (a.CONNECTEDELEMENTID is null and b.CONNECTEDELEMENTID is not null) or (a.CONNECTEDELEMENTID is not null and b.CONNECTEDELEMENTID is null)"
                              + scopeConditionJoin;

         DataTable connRes = queryMultipleRows(connReport, "New and Deleted Connection");
         qResults.Add(connRes);

         string connAttrReport = "Select a.CONNECTINGELEMENTID \"Connecting Elem ID (New)\", a.CONNECTINGELEMENTTYPE \"Connecting Type (New)\", a.CONNECTEDELEMENTID \"Connected Elem ID(New)\", a.CONNECTEDELEMENTTYPE \"Connected Type (New)\", "
                                 + "a.connectingelementattrname \"Connecting Attr Name (New)\", a.connectingelementattrvalue \"Connecting Attr Value (New)\", a.connectedelementattrname \"Connected Attr Name (New)\", a.connectedelementattrvalue \"Connected Attr Value (New)\", "
                                 + "a.connectionattrname \"Connection Attr Name (New)\", a.connectionattrvalue \"Connection Attr Value (New)\", "
                                 + "b.CONNECTINGELEMENTID \"Connecting Elem ID (Ref)\", b.CONNECTINGELEMENTTYPE \"Connecting Type (Ref)\", b.CONNECTEDELEMENTID \"Connected Elem ID(Ref)\", b.CONNECTEDELEMENTTYPE \"Connected Type (Ref)\", "
                                 + "b.connectingelementattrname \"Connecting Attr Name (Ref)\", b.connectingelementattrvalue \"Connecting Attr Value (Ref)\", b.connectedelementattrname \"Connected Attr Name (Ref)\", b.connectedelementattrvalue \"Connected Attr Value (Ref)\", "
                                 + "b.connectionattrname \"Connection Attr Name (Ref)\", b.connectionattrvalue \"Connection Attr Value (Ref)\" from " + connTableNew + " a full outer join "
                                 + connTableRef + " b on (a.CONNECTINGELEMENTID = b.CONNECTINGELEMENTID and a.CONNECTEDELEMENTID = b.CONNECTEDELEMENTID and a.connectingelementattrname = b.connectingelementattrname and a.connectedelementattrname = b.connectedelementattrname) "
                                 + "where a.connectingelementattrvalue != b.connectingelementattrvalue or a.connectedelementattrvalue != b.connectedelementattrvalue or(a.connectingelementattrvalue is null and b.connectingelementattrvalue is not null) or(a.connectedelementattrvalue is not null and b.connectedelementattrvalue is null)"
                                 + scopeConditionJoin;

         DataTable connAttrRes = queryMultipleRows(connAttrReport, "Connection Atrribute Changes");
         qResults.Add(connAttrRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffElementDependency()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string dependTableNew = DBOperation.formatTabName("BIMRL_ELEMENTDEPENDENCY", compNewModel.Value);
         string dependTableRef = DBOperation.formatTabName("BIMRL_ELEMENTDEPENDENCY", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.elementid in (select elementid from " + scopeTable + ") and b.elementid in (select elementid from " + scopeTable + ")";
         }

         DBOperation.beginTransaction();
         string dependReport = "Select a.elementid \"Element ID (New)\", a.elementtype \"Element Type (New)\", a.DEPENDENTELEMENTID \"Dependent Element ID (New)\", a.DEPENDENTELEMENTTYPE \"Dependent Element Type (New)\", a.dependencytype \"Dependency Type (New)\", "
                                 + "b.elementid \"Element ID (Ref)\", b.elementtype \"Element Type (Ref)\", b.DEPENDENTELEMENTID \"Dependent Element ID (Ref)\", b.DEPENDENTELEMENTTYPE \"Dependent Element Type (Ref)\", b.dependencytype \"Dependency Type (Ref)\" "
                                 + "from " + dependTableNew + " a full outer join " + dependTableRef + " b on (a.elementid=b.elementid and a.dependentelementid=b.dependentelementid) "
                                 + "where (a.dependentelementid is null and b.dependentelementid is not null) or (a.dependentelementid is not null and b.dependentelementid is null)"
                                 + scopeConditionJoin;

         DataTable dependRes = queryMultipleRows(dependReport, "Element Dependency Changes");
         qResults.Add(dependRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      IList<DataTable> DiffSpaceBoundary()
      {
         IList<DataTable> qResults = new List<DataTable>();
         string spacebTableNew = DBOperation.formatTabName("BIMRL_RELSPACEBOUNDARY", compNewModel.Value);
         string spacebTableRef = DBOperation.formatTabName("BIMRL_RELSPACEBOUNDARY", compRefModel.Value);
         string scopeConditionNew = "";
         string scopeConditionDel = "";
         string scopeConditionJoin = "";
         if (hasScope)
         {
            scopeConditionNew = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionDel = " and elementid in (select elementid from " + scopeTable + ")";
            scopeConditionJoin = " and a.boundaryelementid in (select elementid from " + scopeTable + ") and b.boundaryelementid in (select elementid from " + scopeTable + ")";
         }

         DBOperation.beginTransaction();
         string spacebReport = "select a.spaceelementid \"Space ID (New)\", a.boundaryelementid \"Boundary ID (New)\", a.boundaryelementtype \"Boundary Elem Type (New)\", a.boundarytype \"Boundary Type (New)\", a.internalorexternal \"Internal or External (New)\", "
                                 + "b.spaceelementid \"Space ID (Ref)\", b.boundaryelementid \"Boundary ID (Ref)\", b.boundaryelementtype \"Boundary Elem Type (Ref)\", b.boundarytype \"Boundary Type (Ref)\", b.internalorexternal \"Internal or External (Ref)\" "
                                 + "from " + spacebTableNew + " a full outer join " + spacebTableRef + " b on(a.spaceelementid = b.spaceelementid and a.boundaryelementid = b.boundaryelementid) "
                                 + "where a.boundaryelementid != b.boundaryelementid or (a.boundaryelementid is null and b.boundaryelementid is not null) or (a.boundaryelementid is not null and b.boundaryelementid is null)"
                                 + scopeConditionJoin;

         DataTable spacebRes = queryMultipleRows(spacebReport, "Space Boundary Changes");
         qResults.Add(spacebRes);

         DBOperation.rollbackTransaction();
         return qResults;
      }

      public DataTable queryMultipleRows(string sqlStmt, string tabName)
      {
         if (String.IsNullOrEmpty(sqlStmt))
            return null;

         DBOperation.beginTransaction();
         DataTable queryDataTableBuffer = new DataTable(tabName);

#if ORACLE
         OracleCommand command = new OracleCommand(sqlStmt, DBOperation.DBConn);
#endif
#if POSTGRES
         NpgsqlCommand command = new NpgsqlCommand(sqlStmt, DBOperation.DBConn);
#endif
         // TODO!!!!! This one still gives mysterious error if the "alias".* on BIMRLEP$<var> has different column list in previous statement
         // The * seems to "remember" the earlier one. If the number of columns are shorter than the previous one, it will throw OracleException for the "missing"/unrecognized column name
         try
         {
#if ORACLE
            OracleDataReader reader = command.ExecuteReader();
#endif
#if POSTGRES
            NpgsqlDataReader reader = command.ExecuteReader();
#endif
            queryDataTableBuffer.Load(reader);
         }

#if ORACLE
         catch (OracleException e)
#endif
#if POSTGRES
         catch (NpgsqlException e)
#endif
         {
            string excStr = "%%Error - " + e.Message + "\n" + command.CommandText;
            m_BIMRLCommonRef.StackPushError(excStr);
            if (DBOperation.UIMode)
            {
               BIMRLErrorDialog erroDlg = new BIMRLErrorDialog(m_BIMRLCommonRef);
               erroDlg.ShowDialog();
            }
            else
               Console.Write(m_BIMRLCommonRef.ErrorMessages);
         }

         command.Dispose();
         DBOperation.rollbackTransaction();
         return queryDataTableBuffer;
      }

      public int runNonQuery(string sqlStmt, bool ignoreError, bool commit=false)
      {
         if (String.IsNullOrEmpty(sqlStmt))
            return 0;

         DBOperation.beginTransaction();
#if ORACLE
         OracleCommand command = new OracleCommand(sqlStmt, DBOperation.DBConn);
#endif
#if POSTGRES
         NpgsqlCommand command = new NpgsqlCommand(sqlStmt, DBOperation.DBConn);
#endif
         try
         {
            int status = command.ExecuteNonQuery();
            if (commit)
               DBOperation.commitTransaction();
            else
               DBOperation.rollbackTransaction();
            return status;
         }
#if ORACLE
         catch (OracleException e)
#endif
#if POSTGRES
         catch (NpgsqlException e)
#endif
         {
            if (ignoreError)
            {
               command.Dispose();
               DBOperation.rollbackTransaction();
               return 0;
            }
            string excStr = "%%Error - " + e.Message + "\n" + command.CommandText;
            m_BIMRLCommonRef.StackPushError(excStr);
            command.Dispose();
            DBOperation.rollbackTransaction();
            return 0;
         }
      }

      void AddResultDict(string name, IList<DataTable> diffResults)
      {
         foreach(DataTable tab in diffResults)
         {
            diffResultsDict.Add(name + ": " + tab.TableName, tab);
         }
      }

      void DefineScope()
      {
         // Makes no sense to define scope where no element nor geometry scope is defined
         if (string.IsNullOrEmpty(ScopeElement) && ScopeGeom == null)
         {
            hasScope = false;
            return;
         }

         string stmtNew = "select distinct sa.elementid from " + DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value) + " sa";
         string stmtNewP = "";
         string stmtNewG = "";
         string stmtRef = "select distinct sa.elementid from " + DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value) + " sa";
         string stmtRefP = "";
         string stmtRefG = "";

         if (!string.IsNullOrEmpty(ScopeElement))
         {
            // Define a scope by level or spaces with condition
            string elemscope = "";
            if (ScopeElement.Equals("Level"))
            {
               elemscope = " upper(sa.elementtype) in ('IFCBUILDINGSTOREY','OST_LEVELS') ";
            }
            else if (ScopeElement.Equals("Space"))
            {
               elemscope = " upper(sa.elementtype) in ('IFCSPACE','OST_ROOMS','OST_MEPSPACES','OST_AREAS') ";
            }

            // Check scope condition. It only make sense when the scope element is defined
            string condition = null;
            if (!string.IsNullOrEmpty(ScopeCondition))
            {
               if (ScopeCondition.StartsWith("Property", StringComparison.CurrentCultureIgnoreCase))
               { 
                  condition = ScopeCondition.Remove(ScopeCondition.Length - 1, 1).Remove(0, 8).Trim().Remove(0, 1);
                  stmtNewP = stmtNew + ", " + DBOperation.formatTabName("BIMRL_PROPERTIES", compNewModel.Value) + " sb ";
                  BIMRLCommon.appendToString("sa.elementid=sb.elementid", " where ", ref stmtNewP);
                  BIMRLCommon.appendToString(elemscope, " and ", ref stmtNewP);
                  BIMRLCommon.appendToString(condition, " and ", ref stmtNewP);

                  stmtRefP = stmtRef + ", " + DBOperation.formatTabName("BIMRL_PROPERTIES", compRefModel.Value) + " sb ";
                  BIMRLCommon.appendToString("sa.elementid=sb.elementid", " where ", ref stmtRefP);
                  BIMRLCommon.appendToString(elemscope, " and ", ref stmtRefP);
                  BIMRLCommon.appendToString(condition, " and ", ref stmtRefP);
               }
               else
               {
                  stmtNewP = stmtNew;
                  stmtRefP = stmtRef;
                  BIMRLCommon.appendToString(elemscope, " and ", ref stmtNewP);
                  BIMRLCommon.appendToString(condition, " and ", ref stmtNewP);

                  BIMRLCommon.appendToString(elemscope, " and ", ref stmtRefP);
                  BIMRLCommon.appendToString(condition, " and ", ref stmtRefP);
               }
            }

            stmtNewP = "select elementid from " + DBOperation.formatTabName("BIMRL_ELEMENT", compNewModel.Value)
                        + " where container in (" + stmtNewP + ")";
            stmtRefP = "select elementid from " + DBOperation.formatTabName("BIMRL_ELEMENT", compRefModel.Value)
                        + " where container in (" + stmtRefP + ")";
         }

         // If Bounding box scope is defined, use it to limit the search using spatial index
         if (ScopeGeom != null)
         {
            try
            {
               int newllbxidx, newllbyidx, newllbzidx, newurtxidx, newurtyidx, newurtzidx;
               int new2llbxidx, new2llbyidx, new2llbzidx, new2urtxidx, new2urtyidx, new2urtzidx;
               int refllbxidx, refllbyidx, refllbzidx, refurtxidx, refurtyidx, refurtzidx;
               int ref2llbxidx, ref2llbyidx, ref2llbzidx, ref2urtxidx, ref2urtyidx, ref2urtzidx;

               stmtNewG = stmtNew;
               stmtRefG = stmtRef;

               Point3D wbbLLBnew;
               Point3D wbbURTnew;
               DBOperation.getWorldBB(compNewModel.Value, out wbbLLBnew, out wbbURTnew);

               Point3D wbbLLBref;
               Point3D wbbURTref;
               DBOperation.getWorldBB(compRefModel.Value, out wbbLLBref, out wbbURTref);

               Octree.WorldBB = new BoundingBox3D(wbbLLBnew, wbbURTnew);
               Octree.MaxDepth = 19;
               CellID64 llbxxsCellnew = CellID64.cellAtMaxDepth(ScopeGeom.LLB);
               CellID64.getCellIDComponents(llbxxsCellnew, out newllbxidx, out newllbyidx, out newllbzidx, out newurtxidx, out newurtyidx, out newurtzidx);

               CellID64 urtxxsCellnew = CellID64.cellAtMaxDepth(ScopeGeom.URT);
               CellID64.getCellIDComponents(urtxxsCellnew, out new2llbxidx, out new2llbyidx, out new2llbzidx, out new2urtxidx, out new2urtyidx, out new2urtzidx);

               Octree.WorldBB = new BoundingBox3D(wbbLLBref, wbbURTref);
               Octree.MaxDepth = 19;
               CellID64 llbxxsCellref = CellID64.cellAtMaxDepth(ScopeGeom.LLB);
               CellID64.getCellIDComponents(llbxxsCellnew, out refllbxidx, out refllbyidx, out refllbzidx, out refurtxidx, out refurtyidx, out refurtzidx);

               CellID64 urtxxsCellref = CellID64.cellAtMaxDepth(ScopeGeom.URT);
               CellID64.getCellIDComponents(urtxxsCellref, out ref2llbxidx, out ref2llbyidx, out ref2llbzidx, out ref2urtxidx, out ref2urtyidx, out ref2urtzidx);

               BIMRLCommon.appendToString(DBOperation.formatTabName("BIMRL_SPATIALINDEX", compNewModel.Value) + " sc ", ", ", ref stmtNewG);
               BIMRLCommon.appendToString("sc.elementid=sa.elementid and sc.xminbound>=" + newllbxidx.ToString() + " and sc.yminbound>=" + newllbyidx.ToString() 
                                             + " and sc.zminbound>=" + newllbzidx.ToString() + " and sc.xmaxbound<=" + new2urtxidx.ToString() 
                                             + " and sc.ymaxbound<=" + new2urtyidx.ToString() + " and sc.zmaxbound<=" + new2urtzidx.ToString(), " where ",
                                             ref stmtNewG);

               BIMRLCommon.appendToString(DBOperation.formatTabName("BIMRL_SPATIALINDEX", compRefModel.Value) + " sc ", ", ", ref stmtRefG);
               BIMRLCommon.appendToString("sc.elementid=sa.elementid and sc.xminbound>=" + refllbxidx.ToString() + " and sc.yminbound>=" + refllbyidx.ToString() 
                                             + " and sc.zminbound>=" + refllbzidx.ToString() + " and sc.xmaxbound<=" + ref2urtxidx.ToString() 
                                             + " and sc.ymaxbound<=" + ref2urtyidx.ToString() + " and sc.zmaxbound<=" + ref2urtzidx.ToString(), " where ",
                                             ref stmtRefG);
            }
            catch
            {
               // Ignore any error
            }
         }

         DBOperation.beginTransaction();
         // Create tables to keep track of the new or deleted objects
         string queryNew = "";
         if (!string.IsNullOrEmpty(stmtNewP) && !string.IsNullOrEmpty(stmtNewG))
            queryNew = stmtNewP + " intersect " + stmtNewG;
         else if (string.IsNullOrEmpty(stmtNewG))
            queryNew = stmtNewP;
         else if (string.IsNullOrEmpty(stmtNewP))
            queryNew = stmtNewG;

         string queryRef = "";
         if (!string.IsNullOrEmpty(stmtRefP) && !string.IsNullOrEmpty(stmtRefG))
            queryRef = stmtRefP + " intersect " + stmtRefG;
         else if (string.IsNullOrEmpty(stmtRefG))
            queryRef = stmtRefP;
         else if (string.IsNullOrEmpty(stmtRefP))
            queryRef = stmtRefG;

         string queryUnion = "(" + queryNew + ") union (" + queryRef + ")";

         //runNonQuery("drop table " + scopeNewTable, true, doCommit);
         //runNonQuery("create table " + scopeNewTable + " as " + queryNew, true, doCommit);
         //runNonQuery("drop table " + scopeRefTable, true, doCommit);
         //runNonQuery("create table " + scopeRefTable + " as " + queryRef, true, doCommit);
         runNonQuery("drop table " + scopeTable, true, doCommit);
         runNonQuery("create table " + scopeTable + " as " + queryUnion, true, doCommit);

         hasScope = true;
         DBOperation.commitTransaction();
         return;
      }
   }
}
