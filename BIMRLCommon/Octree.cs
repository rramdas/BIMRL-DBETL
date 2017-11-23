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
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data;
using System.Threading.Tasks;
using System.Runtime.Serialization.Formatters.Binary;
using System.Runtime.Serialization;
#if ORACLE
using Oracle.DataAccess.Types;
using Oracle.DataAccess.Client;
#endif
#if POSTGRES
using Npgsql;
#endif

namespace BIMRL.Common
{
   public sealed class Octree
   {
      static BoundingBox3D _WorldBB;
      static int _maxDepth = 6;
      //OctreeNode theTree;

      static Dictionary<UInt64, CellData> masterDict;
      //static Dictionary<string, Int16> masterDictDB;

      // A Pair of Dict and List to allow fast access to the index tat will be stored into a celldata
      static Dictionary<Tuple<Guid, int>, int> elemIDDict;  // Keeping the list of element ids in a Dictionary for int value of an index in the List
      //static List<Tuple<Guid, int>> elemIDList;
      static IDictionary<int, Tuple<Guid, int>> elemIDList;
      List<string> candidates;
      Dictionary<UInt64, CellData> userDict;
      static int _ID = -1;
      public bool userDictKeepOriginalCell { get; set; }

      [Serializable]
      public struct CellData
      {
         public byte nodeType;   // node=0, leaf=1
         //public Dictionary<string, int> data;
         public SortedSet<int> data;
      }

      public enum OctreeCheck
      {
         NOTFOUND,
         NODEFOUND,
         FOUNDANCESTOR,
         FOUNDDESCENDANT
      }

      enum OctreeCellType
      {
         NODE = 0,
         LEAF = 1,
         LEAFWITHANCESTOR = 8,
         LEAFWITHDESCENDANT = 9
      }

      public Dictionary<UInt64, CellData> MasterDict
      {
         get { return masterDict; }
      }

      public IDictionary <int, Tuple<Guid, int>> ElemIDList
      {
         get { return elemIDList; }
      }

      public Octree(int ID, int? initDictNo, int? maxDepth)
      {
         int initDictSize = 100000;      // arbitrary default initial number for the dictionary
         int maxOctreeDepth = 3;         // initial max level following the UI setting

         if (initDictNo.HasValue)
            if (initDictNo.Value > 0)
               initDictSize = initDictNo.Value * 10;    // reserve 10x of the initial number of records of elementid
         if (maxDepth.HasValue)
            if (maxDepth.Value > 0)
               maxOctreeDepth = maxDepth.Value;

         init(ID, initDictSize, maxOctreeDepth, false, false);
      }

      public Octree(int ID, int? initDictNo, int? maxDepth, bool forUserDict)
      {
         int initDictSize = 100000;      // arbitrary default initial number for the dictionary
         int maxOctreeDepth = 3;         // initial max level following the UI setting

         if (initDictNo.HasValue)
            if (initDictNo.Value > 0)
               initDictSize = initDictNo.Value * 10;
         if (maxDepth.HasValue)
            if (maxDepth.Value > 0)
               maxOctreeDepth = maxDepth.Value;

         init(ID, initDictSize, maxOctreeDepth, forUserDict, false);
      }

      public Octree(int ID, int? initDictNo, int? maxDepth, bool forUserDict, bool skipRegenDict)
      {
         int initDictSize = 100000;      // arbitrary default initial number for the dictionary
         int maxOctreeDepth = 3;         // initial max level following the UI setting

         if (initDictNo.HasValue)
            if (initDictNo.Value > 0)
               initDictSize = initDictNo.Value * 10;
         if (maxDepth.HasValue)
            if (maxDepth.Value > 0)
               maxOctreeDepth = maxDepth.Value;

         init(ID, initDictSize, maxOctreeDepth, forUserDict, skipRegenDict);
      }

      void init(int ID, int initDictNo, int maxDepth, bool forUserDict, bool skipRegenDict)
      {
         if (_ID != ID || masterDict == null || masterDict.Count == 0)
         {
            _ID = ID;
            {
               // If not empty, clear first before reallocating a new ones
               if (masterDict != null)
                  masterDict.Clear();
               if (elemIDDict != null)
                  elemIDDict.Clear();
               if (elemIDList != null)
                  elemIDList.Clear();

               masterDict = new Dictionary<UInt64, CellData>(initDictNo);
               elemIDDict = new Dictionary<Tuple<Guid, int>, int>(initDictNo);
               elemIDList = new Dictionary<int, Tuple<Guid, int>>(initDictNo);

               CellID64 cell = new CellID64("000000000000");
               masterDict.Add(cell.iCellID, new CellData { nodeType = 0, data = new SortedSet<int>() });
               if (!skipRegenDict)
               {
                  regenSpatialIndexDict(ID, ref masterDict);
               }
            }
            _maxDepth = maxDepth;
            candidates = new List<string>();
         }

         if (forUserDict)
         {
            if (userDict != null)
               userDict.Clear();

            userDict = new Dictionary<UInt64, CellData>(1000);
            userDictKeepOriginalCell = false;
         }
      }

      public static int MaxDepth
      {
         get { return _maxDepth; }
         set { _maxDepth = value; }
      }

      public static BoundingBox3D WorldBB
      {
         set { _WorldBB = value; }
         get { return _WorldBB; }
      }

      public static List<string> parentCellList(CellID64 cellID)
      {
         List<string> cidList = new List<string>();
         CellID64 pCell = cellID;
         for (int i = CellID64.getLevel(pCell); i > 0; i-- )
         {
            pCell = CellID64.parentCell(pCell);
            cidList.Add(pCell.ToString());
         }
         return cidList;
      }

      public static string parentsCellCondition(CellID64 cellID)
      {
         string tmpStr = "";
         CellID64 pCell = cellID;

         for (int i = CellID64.getLevel(cellID); i > 0; i--)
         {
            if (i < CellID64.getLevel(cellID))
               tmpStr += " OR ";
            pCell = CellID64.parentCell(pCell);
            tmpStr += "(CELLID = '" + pCell.ToString() + "' OR CELLID = '";
            pCell.setBorderCell();
            tmpStr += pCell.ToString() + "')";
         }
         return tmpStr;
      }

      public static string childrenCellCondition(CellID64 cellID)
      {
         string cellIDStr = cellID.ToString();
         int usedCharIdx = (int) Math.Ceiling( (double) (CellID64.getLevel(cellID) / 2));
         string tmpStr = "(CELLID LIKE '" + cellIDStr.Substring(0, usedCharIdx) + "%' AND CELLID > '" + cellIDStr + "')";

         return tmpStr;
      }

      public void ComputeOctree(string elementID, Polyhedron polyH)
      {
         ComputeOctree(elementID, polyH, false);
      }

      /// <summary>
      /// Compute Octree for a Polyhedron
      /// </summary>
      /// <param name="elementID"></param>
      /// <param name="polyH"></param>
      /// <param name="forUserDict"></param>
      public void ComputeOctree(string elementID, Polyhedron polyH, bool forUserDict)
      {
         Tuple<Guid, int> elementIDNo = ElementID.ElementIDstrToKey(elementID, forUserDict);

         OctreeNode theTree = new OctreeNode();
         // Do it in steps:
         // 1. Find the smallest containing cell based on the PolyH BB, it it to quickly eliminate the irrelevant cells very quickly
         theTree.nodeCellID = OctreeNodeProcess.getSmallestContainingCell(polyH);
         theTree._depth = theTree.nodeCellID.Level;

         // 2. Perform subdivision using the BB first: quick division since there is not expensive intersection. It leaves all the leaves based on BB
         OctreeNodeProcess.ProcessBB(theTree, polyH.boundingBox);

         // 3. Evaluate each leaf nodes for further subdivision using the actual polyhedron (the original algorithm)
         OctreeNodeProcess.Process(theTree, polyH);

         List<CellID64> collCellID;
         List<int> collBorderFlag;
         OctreeNodeProcess.collectCellIDs(theTree, out collCellID, out collBorderFlag);
         for (int i=0; i < collCellID.Count; ++i)
         {
            if (forUserDict)
               insertDataToUserDict(elementIDNo, collCellID[i], collBorderFlag[i], false);
            else
               //insertDataToDictDB(elementID, collCellID[i]);
               insertDataToDict(elementIDNo, collCellID[i]);
         }
      }

      /// <summary>
      /// Compute Octree for a face
      /// </summary>
      /// <param name="elementID"></param>
      /// <param name="face"></param>
      /// <param name="forUserDict"></param>
      public void ComputeOctree(string elementID, Face3D face, bool forUserDict)
      {
         Tuple<Guid, int> elementIDNo = ElementID.ElementIDstrToKey(elementID, forUserDict);

         OctreeNode theTree = new OctreeNode();
            
         // Add a step:
         // 1. Find the smallest containing cell based on the Face BB, it it to quickly eliminate the irrelevant cells very quickly
         theTree.nodeCellID = OctreeNodeProcess.getSmallestContainingCell(face);
         theTree._depth = theTree.nodeCellID.Level;

         OctreeNodeProcess.Process(theTree, face);
         List<CellID64> collCellID;
         List<int> collBorderFlag;
         OctreeNodeProcess.collectCellIDs(theTree, out collCellID, out collBorderFlag);
         for (int i = 0; i < collCellID.Count; ++i)
         {
            if (forUserDict)
               insertDataToUserDict(elementIDNo, collCellID[i], collBorderFlag[i], false);
            else
               //insertDataToDictDB(elementID, collCellID[i]);
               insertDataToDict(elementIDNo, collCellID[i]);
         }
      }

      /// <summary>
      /// Compute Octree for a Line Segment
      /// </summary>
      /// <param name="elementID"></param>
      /// <param name="lineS"></param>
      /// <param name="forUserDict"></param>
      public void ComputeOctree(string elementID, LineSegment3D lineS, bool forUserDict)
      {
         Tuple<Guid, int> elementIDNo = ElementID.ElementIDstrToKey(elementID, forUserDict);

         OctreeNode theTree = new OctreeNode();

         // Add a step:
         // 1. Find the smallest containing cell based on the Face BB, it it to quickly eliminate the irrelevant cells very quickly
         theTree.nodeCellID = OctreeNodeProcess.getSmallestContainingCell(lineS);
         theTree._depth = theTree.nodeCellID.Level;

         OctreeNodeProcess.Process(theTree, lineS);
         List<CellID64> collCellID;
         List<int> collBorderFlag;
         OctreeNodeProcess.collectCellIDs(theTree, out collCellID, out collBorderFlag);
         for (int i = 0; i < collCellID.Count; ++i)
         {
            if (forUserDict)
               insertDataToUserDict(elementIDNo, collCellID[i], collBorderFlag[i], false);
            else
               insertDataToDict(elementIDNo, collCellID[i]);
         }
      }

      void insertDataToDict(Tuple<Guid, int> elementID, CellID64 cellID)
      {
         CellData cellData;
         if (!masterDict.TryGetValue(cellID.iCellID, out cellData))
         {
            // no entry yet for this cell
            createCellInDict(elementID, cellID);
            masterDict.TryGetValue(cellID.iCellID, out cellData);
         }

         if (cellData.nodeType == 1)         //it's leaf, add the elementID
         {
            if (cellData.data == null)
            {
               cellData.data = new SortedSet<int>();
            }

            cellData.data.Add(getIndexForElementID(elementID));
         }
         else   // it's a node, we must traverse down and add elementID to all the leaves. Not ideal to pass the same flag, but better than none
         {
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 0));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 1));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 2));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 3));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 4));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 5));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 6));
            insertDataToDict(elementID, CellID64.newChildCellId(cellID, 7));
         }
      }

   void createCellInDict(Tuple<Guid, int> elementID, CellID64 cellID)
   {
      CellID64 parentID = CellID64.parentCell(cellID);
      CellData cellData;
      if (!masterDict.TryGetValue(parentID.iCellID, out cellData))
      {
         createCellInDict(elementID, parentID);
         masterDict.TryGetValue(parentID.iCellID, out cellData);
      }

      try
      {
         // entry found, need to create all the entries for the children and transfer all the data into the new cells
         // remove the current elementid in the data first if present. It will be added later on
         if (cellData.data != null)
         {
            cellData.data.Remove(getIndexForElementID(elementID));
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 0).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 0).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 1).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 1).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 2).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 2).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 3).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 3).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 4).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 4).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 5).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 5).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 6).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 6).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 7).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 7).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>(cellData.data) });
            // reset cellData and set the nodeType to "node"
            cellData.data.Clear();
         }
         else
         {
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 0).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 0).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 1).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 1).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 2).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 2).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 3).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 3).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 4).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 4).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 5).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 5).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 6).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 6).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
            if (!masterDict.ContainsKey(CellID64.newChildCellId(parentID, 7).iCellID))
               masterDict.Add(CellID64.newChildCellId(parentID, 7).iCellID, new CellData { nodeType = 1, data = new SortedSet<int>() });
         }
         cellData.nodeType = 0;
         masterDict[parentID.iCellID] = cellData;
      }
      catch (Exception ex)
      {
         refCellBIMRLCommon.StackPushError(ex.Message);
         throw;
      }
   }

      static BIMRLCommon refCellBIMRLCommon = new BIMRLCommon();

//      void insertDataToDictDB(string elementID, CellID64 cellID)
//      {
//         CellData cellData;
//         cellData.nodeType = 1;
//         string sqlStmt = null;

//         short cellType;
//         try
//         {
//            if (!masterDictDB.TryGetValue(cellID.ToString(), out cellType))
//            {
//               // no entry yet for this cell
//               createCellInDictDB(elementID, cellID);
//               masterDictDB.TryGetValue(cellID.ToString(), out cellType);
//            }

//            if (cellType == 1)         //it's leaf, add the elementID
//            {
//               DBOperation.executeSingleStmt("INSERT INTO CELLTREEDETTMP (CELLID,ELEMENTID) VALUES ('" + cellID.ToString() + "','" + elementID.ToString() + "')" );
//            }
//            else   // it's a node, we must traverse down and add elementID to all the leaves. Not ideal to pass the same flag, but better than none
//            {
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 0));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 1));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 2));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 3));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 4));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 5));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 6));
//               insertDataToDictDB(elementID, CellID64.newChildCellId(cellID, 7));
//            }
//         }
//#if ORACLE
//         catch (OracleException e)
//#endif
//#if POSTGRES
//         catch (NpgsqlException e)
//#endif
//         {
//            string excStr = "%%Read Error - " + e.Message + "\n\t" + sqlStmt;
//            refCellBIMRLCommon.StackPushError(excStr);
//         }
//      }

//      void createCellInDictDB(string elementID, CellID64 cellID)
//      {
//         CellID64 parentID = CellID64.parentCell(cellID);
//         string sqlStmt = null;

//         try
//         {
//            short cellType;
//            if (!masterDictDB.TryGetValue(parentID.ToString(), out cellType))
//            {
//               createCellInDictDB(elementID, parentID);
//               masterDictDB.TryGetValue(parentID.ToString(), out cellType);
//            }

//            DBOperation.executeSingleStmt("DELETE FROM CELLTREEDETTMP WHERE CELLID='" + parentID.ToString() + "' AND ELEMENTID='" + elementID.ToString() + "'");
//            for (int i = 0; i < 8; ++i)
//            {
//               string childID = CellID64.newChildCellId(parentID, i).ToString();
//               if (!masterDictDB.ContainsKey(childID))
//                  masterDictDB.Add(childID, 1);

//               DBOperation.executeSingleStmt("INSERT INTO CELLTREEDETTMP (CELLID,ELEMENTID) SELECT '" + childID 
//                  + "',ELEMENTID FROM CELLTREEDETTMP WHERE CELLID='" + parentID.ToString() +"'");
//            }
//            // reset cellData and set the nodeType to "node"
//            masterDictDB[parentID.ToString()] = 0;
//            DBOperation.executeSingleStmt("DELETE FROM CELLTREEDETTMP WHERE CELLID='" + parentID.ToString() + "'");
//         }
//#if ORACLE
//      catch (OracleException e)
//#endif
//#if POSTGRES
//      catch (NpgsqlException e)
//#endif
//      {
//         string excStr = "%%Read Error - " + e.Message + "\n\t" + sqlStmt;
//               refCellBIMRLCommon.StackPushError(excStr);
//         }
//      }

      void insertDataToUserDict(Tuple<Guid, int> elementID, CellID64 cellID, int borderFlag, bool traverseDepth)
      {
         CellData cellData;
         List<UInt64> foundID;
         OctreeCheck retEnum = findNodeInDict(cellID.iCellID, traverseDepth, out foundID);

         if (retEnum == OctreeCheck.NOTFOUND)
         {
            SortedSet<int> data = new SortedSet<int>();
            byte cellType = (byte)OctreeCellType.LEAF;
            data.Add(getIndexForElementID(elementID));     // borderflag is not used anymore
            cellData = new CellData { nodeType = cellType, data = data };
            userDict.Add(cellID.iCellID, cellData);
         }
         else if (retEnum == OctreeCheck.NODEFOUND)
         {
            cellData = masterDict[cellID.iCellID];
            if (cellData.nodeType == 1)         //it's leaf, add the elementID
            {
               SortedSet<int> iData = new SortedSet<int>();
               byte cellType = (byte)OctreeCellType.LEAF;
               iData.Add(getIndexForElementID(elementID));
               CellData cdata;
               if (!userDict.TryGetValue(cellID.iCellID, out cdata))
               {
                  // entry is not found in the userdict yet
                  cdata = new CellData();
                  cdata.nodeType = cellType;
                  cdata.data = iData;
                  userDict.Add(cellID.iCellID, cdata);
               }
               else
               {
                  if (cdata.data == null)
                  {
                        cdata.data = new SortedSet<int>();
                  }
                  cdata.data.Add(getIndexForElementID(elementID));
               }
            }
            else   // it's a node, we must traverse down and add elementID to all the leaves. Not ideal to pass the same flag, but better than none
            {
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 0), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 1), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 2), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 3), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 4), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 5), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 6), borderFlag, true);
               insertDataToUserDict(elementID, CellID64.newChildCellId(cellID, 7), borderFlag, true);
            }
         }
         else if (retEnum == OctreeCheck.FOUNDANCESTOR || retEnum == OctreeCheck.FOUNDDESCENDANT)
         {
            // Add the current ID entry into the userDict
            SortedSet<int> data = new SortedSet<int>();

            byte cellType = (byte) OctreeCellType.LEAF;
            if (retEnum == OctreeCheck.FOUNDANCESTOR)
               cellType = (int) OctreeCellType.LEAFWITHANCESTOR;
            if (retEnum == OctreeCheck.FOUNDDESCENDANT)
               cellType = (int) OctreeCellType.LEAFWITHDESCENDANT;

            if (userDictKeepOriginalCell)
            {
               //data.Add(elementID, borderFlag);
               data.Add(getIndexForElementID(elementID));
               cellData = new CellData { nodeType = cellType, data = data };
               userDict.Add(cellID.iCellID, cellData);
            }
            // Now loop through the Ancestor ID(s) found and create new entry(ies) in the userDict
            foreach (UInt64 id in foundID)
            {
               CellData cData;
               if (!userDict.TryGetValue(id, out cData))
               {
                  SortedSet<int> iData = new SortedSet<int>();
                  iData.Add(getIndexForElementID(elementID));
                  cData = new CellData { nodeType = (int)OctreeCellType.LEAF, data = iData };
                  userDict.Add(id, cData);
               }
               else
               {
                  if (cData.data == null)
                  {
                        cData.data = new SortedSet<int>();
                  }
                  cData.data.Add(getIndexForElementID(elementID));
               }
            }
         }
      }

      OctreeCheck findNodeInDict(UInt64 nodeID, bool depthOnly, out List<UInt64> IDsFound)
      {
         IDsFound = new List<UInt64>();
         CellID64 cellid = new CellID64(nodeID);

         if (cellid.Level > MaxDepth)
            return OctreeCheck.NOTFOUND;

         CellData outData;
         if (masterDict.TryGetValue(nodeID, out outData))
         {
            IDsFound.Add(nodeID);
            return OctreeCheck.NODEFOUND;
         }

         // No node found at the exact location
         // 1. try to find ancestor (it is easier to get)

         bool found = false;
         if (!depthOnly)
         {
            while (!found)
            {
               CellID64 parentcell = CellID64.parentCell(cellid);
               if (parentcell.iCellID == 0)
                  break;      // reached root cell, it means not found

               found = masterDict.TryGetValue(parentcell.iCellID, out outData);
               if (found)
                  IDsFound.Add(parentcell.iCellID);
               else
                  cellid = parentcell;
            }
         }
         // if still not found, search for the children
         if (found)
         {
            return OctreeCheck.FOUNDANCESTOR;
         }
         else
         {
            // Reset the cellid to the original cellid and not the overwritten one by "found ancestor" logic 
            cellid = new CellID64(nodeID);
            if (cellid.Level >= Octree.MaxDepth)
               return OctreeCheck.NOTFOUND;

            // We will only search for descendants if it is not at the max depth since node at max depth definitely does not have any descendants
            List<UInt64> outID;
            // OctreeCheck ret = findDescendants(cellid, out outID);        // This version uses DB query
            OctreeCheck ret = findDescendantLeafNodesInDict(cellid, out outID);     // This version uses Dict search
            if (ret == OctreeCheck.FOUNDDESCENDANT)
               IDsFound.AddRange(outID);

            return ret;
         }
      }

      OctreeCheck findDescendantLeafNodesInDict(CellID64 cellid, out List<UInt64> outIDs)
      {
         outIDs = new List<UInt64>();

         if (cellid.Level >= MaxDepth)
               return OctreeCheck.NOTFOUND;

         for (int i = 0; i < 8; ++i)
         {
            CellID64 childID = CellID64.newChildCellId(cellid, i);
            CellData cData;
            bool found = masterDict.TryGetValue(childID.iCellID, out cData);
            if (found)
            {
               if (cData.nodeType == (int)OctreeCellType.LEAF)
               {
                  outIDs.Add(childID.iCellID);
               }
               else if (cData.nodeType == (byte)OctreeCellType.NODE)
               {
                  List<UInt64> outList;
                  OctreeCheck ret = findDescendantLeafNodesInDict(childID, out outList);
                  if (outList.Count > 0)
                        outIDs.AddRange(outList);
               }
            }
            else
            {
               List<UInt64> outList;
               OctreeCheck ret = findDescendantLeafNodesInDict(childID, out outList);
               if (outList.Count > 0)
                  outIDs.AddRange(outList);
            }
         }
         if (outIDs.Count > 0)
            return OctreeCheck.FOUNDDESCENDANT;
         else
            return OctreeCheck.NOTFOUND;
      }

      /// <summary>
      /// Collect ALL the cellids from userDict for populating transient geometry(ies)
      /// </summary>
      /// <param name="elementIDList"></param>
      /// <param name="cellIDStrList"></param>
      /// <param name="borderFlagList"></param>
      public void collectSpatialIndexUserDict(out List<string> elementIDList, out List<string> cellIDStrList, out List<int> XMinB, out List<int> YMinB, out List<int> ZMinB, 
                                             out List<int> XMaxB, out List<int> YMaxB, out List<int> ZMaxB, out List<int> depthList, out List<int> cellType)
      {
         int initArraySize = 50000;

         elementIDList = new List<string>(initArraySize);
         cellIDStrList = new List<string>(initArraySize);
         cellType = new List<int>(initArraySize);
         XMinB = new List<int>(initArraySize);
         YMinB = new List<int>(initArraySize);
         ZMinB = new List<int>(initArraySize);
         XMaxB = new List<int>(initArraySize);
         YMaxB = new List<int>(initArraySize);
         ZMaxB = new List<int>(initArraySize);
         depthList = new List<int>(initArraySize);

         int XMin;
         int YMin;
         int ZMin;
         int XMax;
         int YMax;
         int ZMax;

         foreach (KeyValuePair<UInt64, CellData> dictEntry in userDict)
         {
            CellID64 cellID = new CellID64(dictEntry.Key);
            if (dictEntry.Value.data != null)
            {
               foreach (int tupEID in dictEntry.Value.data)
               {
                  string userGeomID = ElementID.GetElementIDstrFromKey(getElementIDByIndex(tupEID), userGeom:true);

                  elementIDList.Add(userGeomID);
                  cellIDStrList.Add(cellID.ToString());
                  // cellType.Add(eID.Value); 
                  cellType.Add(dictEntry.Value.nodeType);

                  CellID64.getCellIDComponents(cellID, out XMin, out YMin, out ZMin, out XMax, out YMax, out ZMax);
                  XMinB.Add(XMin);
                  YMinB.Add(YMin);
                  ZMinB.Add(ZMin);
                  XMaxB.Add(XMax);
                  YMaxB.Add(YMax);
                  ZMaxB.Add(ZMax);
                  depthList.Add(CellID64.getLevel(cellID));
               }
            }
         }
      }

      OctreeCheck findDescendants(CellID64 cellid, out List<UInt64> IDsFound)
      {
         OctreeCheck ret = getCellDescendants(cellid, _ID, out IDsFound);
         return ret;
      }

      public static void regenSpatialIndexDict(int fedID, ref Dictionary<UInt64, Octree.CellData> regenSpIndexTree)
      {
         BIMRLCommon refBIMRLCommon = new BIMRLCommon();
         string sqlStmt = "SELECT ELEMENTID, CELLID FROM " + DBOperation.formatTabName("BIMRL_SPATIALINDEX", fedID);
         try
         {
#if ORACLE
            OracleCommand cmd = new OracleCommand(sqlStmt, DBOperation.DBConn);
            cmd.FetchSize = 100000;
            OracleDataReader reader = cmd.ExecuteReader();
#endif
#if POSTGRES
            NpgsqlCommand cmd = new NpgsqlCommand(sqlStmt, DBOperation.DBConn);
            NpgsqlDataReader reader = cmd.ExecuteReader();
#endif
            while (reader.Read())
            {
               string elementid = reader.GetString(0);
               //ElementID eID = new ElementID(elementid);
               Tuple<Guid, int> eID = ElementID.ElementIDstrToKey(elementid, false);
               string cellid = reader.GetString(1);
               CellID64 cell = new CellID64(cellid);
               if (!regenSpIndexTree.ContainsKey(cell.iCellID))
               {
                  SortedSet<int> cData = new SortedSet<int>();
                  cData.Add(getIndexForElementID(eID));    // the flag is no longer used, any value doesn't matter
                  Octree.CellData data = new Octree.CellData { nodeType = 1, data = cData };
                  regenSpIndexTree.Add(cell.iCellID, data);
               }
               else
               {
                  Octree.CellData data = regenSpIndexTree[cell.iCellID];
                  SortedSet<int> cData = data.data;
                  cData.Add(getIndexForElementID(eID));
               }
            }
            reader.Dispose();
            cmd.Dispose();
         }
#if ORACLE
         catch (OracleException e)
#endif
#if POSTGRES
         catch (NpgsqlException e)
#endif
         {
            string excStr = "%%Read Error - " + e.Message + "\n\t" + sqlStmt;
            refBIMRLCommon.StackPushError(excStr);
         }
         catch (SystemException e)
         {
            string excStr = "%%Read Error - " + e.Message + "\n\t" + sqlStmt;
            refBIMRLCommon.StackPushError(excStr);
#if POSTGRES
            DBOperation.rollbackTransaction();
#endif
            throw;
         }
      }

      public static Octree.OctreeCheck getCellDescendants(CellID64 cellid, int fedID, out List<UInt64> IDsFound)
      {
         IDsFound = new List<UInt64>();
         Octree.OctreeCheck ret;

         string whereCond = Octree.childrenCellCondition(cellid);
         string sqlStmt = "SELECT DISTINCT CELLID FROM " + DBOperation.formatTabName("BIMRL_SPATIALINDEX", fedID) + " WHERE " + whereCond;
         DataTable dt = new DataTable();
#if ORACLE
         OracleCommand command = new OracleCommand(sqlStmt, DBOperation.DBConn);
         OracleDataAdapter dtAdapter = new OracleDataAdapter(command);
#endif
#if POSTGRES
         NpgsqlCommand command = new NpgsqlCommand(sqlStmt, DBOperation.DBConn);
         NpgsqlDataAdapter dtAdapter = new NpgsqlDataAdapter(command);
#endif
         dtAdapter.Fill(dt);
         if (dt.Rows.Count > 0)
            ret = Octree.OctreeCheck.FOUNDDESCENDANT;
         else
            ret = Octree.OctreeCheck.NOTFOUND;

         foreach (DataRow dtRow in dt.Rows)
         {
            CellID64 cell = new CellID64(dtRow["CELLID"].ToString());
            IDsFound.Add(cell.iCellID);
         }

         return ret;
      }

      public static int getIndexForElementID (Tuple<Guid,int> elemID)
      {
         int theIdx;
         if (!elemIDDict.TryGetValue(elemID, out theIdx))
         {
            theIdx = elemIDList.Count;
            elemIDDict.Add(elemID, theIdx);
            elemIDList.Add(theIdx, elemID);
         }
         return theIdx;
      }

      public static Tuple<Guid,int> getElementIDByIndex(int theIdx)
      {
         return elemIDList[theIdx];
      }
   }
}

