#region Header
// Revit API .NET Labs
//
// Copyright (C) 2007-2019 by Autodesk, Inc.
//
// Permission to use, copy, modify, and distribute this software
// for any purpose and without fee is hereby granted, provided
// that the above copyright notice appears in all copies and
// that both that copyright notice and the limited warranty and
// restricted rights notice below appear in all supporting
// documentation.
//
// AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
// AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
// MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
// DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
// UNINTERRUPTED OR ERROR FREE.
//
// Use, duplication, or disclosure by the U.S. Government is subject to
// restrictions set forth in FAR 52.227-19 (Commercial Computer
// Software - Restricted Rights) and DFAR 252.227-7013(c)(1)(ii)
// (Rights in Technical Data and Computer Software), as applicable.
#endregion // Header

#region Namespaces
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;

using Newtonsoft.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

using DesignAutomationFramework;

using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using System.Diagnostics;

#endregion // Namespaces

namespace ExportMEPSystemGraphs
{


    [Autodesk.Revit.Attributes.Regeneration(Autodesk.Revit.Attributes.RegenerationOption.Manual)]
    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class MEPSystemHandler : IExternalDBApplication
    {
        //public const string FireRating = "Fire Rating";
        //public const string Comments = "Comments";
        public const string NodeLabelTag = "text";


        /// <summary>
        /// Return true to include this system in the
        /// exported system graphs.
        /// </summary>
        static bool IsDesirableSystemPredicate(MEPSystem s)
        {
            return 1 < s.Elements.Size
              && !s.Name.Equals("unassigned")
              && ((s is MechanicalSystem
                  && ((MechanicalSystem)s).IsWellConnected)
                || (s is PipingSystem
                  && ((PipingSystem)s).IsWellConnected)
                || (s is ElectricalSystem
                  && ((ElectricalSystem)s).IsMultipleNetwork));
        }

        /// <summary>
        /// The thee MEP disciplines
        /// </summary>
        public enum MepDomain
        {
            Invalid = -1,
            Mechanical = 0,
            Electrical = 1,
            Piping = 2,
            Count = 3
        }

        static MepDomain GetMepDomain(MEPSystem s)
        {
            return (s is MechanicalSystem) ? MepDomain.Mechanical
              : ((s is ElectricalSystem) ? MepDomain.Electrical
                : ((s is PipingSystem) ? MepDomain.Piping
                  : MepDomain.Invalid));
        }


        public ExternalDBApplicationResult OnStartup(ControlledApplication app)
        {
            DesignAutomationBridge.DesignAutomationReadyEvent += HandleDesignAutomationReadyEvent;
            return ExternalDBApplicationResult.Succeeded;
        }

        public ExternalDBApplicationResult OnShutdown(ControlledApplication app)
        {
            return ExternalDBApplicationResult.Succeeded;
        }

        public void HandleDesignAutomationReadyEvent(object sender, DesignAutomationReadyEventArgs e)
        {
            e.Succeeded = ProcessParameters(e.DesignAutomationData);
        }


        public static bool ProcessParameters(DesignAutomationData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            Application rvtApp = data.RevitApp;
            if (rvtApp == null)
                throw new InvalidDataException(nameof(rvtApp));

            string modelPath = data.FilePath;
            if (String.IsNullOrWhiteSpace(modelPath))
                throw new InvalidDataException(nameof(modelPath));

            Document doc = data.RevitDoc;
            if (doc == null)
                throw new InvalidOperationException("Could not open document.");

            InputParams inputParams = InputParams.Parse("params.json");

            return FindConnectedElements(doc, inputParams);
        }


        public static bool FindConnectedElements(Document doc, InputParams parameters)
        {
            FilteredElementCollector allSystems
              = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPSystem));

            int nAllSystems = allSystems.Count<Element>();

            IEnumerable<MEPSystem> desirableSystems
              = allSystems.Cast<MEPSystem>().Where<MEPSystem>(
                s => IsDesirableSystemPredicate(s));

            int nDesirableSystems = desirableSystems
              .Count<Element>();

            // Check for shared parameter
            // to store graph information.

            // Determine element from which to retrieve
            // shared parameter definition.

            Element json_storage_element
              = !parameters.StoreEntireJsonGraphOnProjectInfo
                ? desirableSystems.First<MEPSystem>()
                : new FilteredElementCollector(doc)
                  .OfClass(typeof(ProjectInfo))
                  .FirstElement();

            Definition def = SharedParameterMgr.GetDefinition(
              json_storage_element);

            if (null == def)
            {
                SharedParameterMgr.Create(doc, parameters.StoreEntireJsonGraphOnProjectInfo);

                def = SharedParameterMgr.GetDefinition(
                  json_storage_element);

                if (null == def)
                {
                    Console.Write("Error creating the storage shared parameter.");
                    return false;
                }
            }

            string outputFolder = Directory.GetCurrentDirectory();

            int nXmlFiles = 0;
            int nJsonGraphs = 0;
            int nJsonBytes = 0;

            // Collect one JSON string per system.

            string json;

            // Three separate collections for mechanical,
            // electrical and piping systems:

            List<string>[] json_collector
              = new List<string>[(int)MepDomain.Count] {
          new List<string>(),
          new List<string>(),
          new List<string>() };

            using (Transaction t = new Transaction(doc))
            {
                t.Start("Determine MEP Graph Structure and Store in JSON Shared Parameter");

                StringBuilder[] sbs = new StringBuilder[3];

                for (int i = 0; i < 3; ++i)
                {
                    sbs[i] = new StringBuilder();
                    sbs[i].Append("[");
                }

                foreach (MEPSystem system in desirableSystems)
                {
                    //Debug.Print(system.Name);
                    Console.WriteLine(system.Name);


                    FamilyInstance root = system.BaseEquipment;

                    // Traverse the system and dump the
                    // traversal graph into an XML file

                    TraversalTree tree = new TraversalTree(system);

                    if (tree.Traverse())
                    {
                        string filename = system.Id.Value.ToString();

                        filename = Path.ChangeExtension(
                          Path.Combine(outputFolder, filename), "xml");

                        tree.DumpIntoXML(filename);

                        // Uncomment to preview the
                        // resulting XML structure

                        //Process.Start( fileName );

                        json = parameters.StoreJsonGraphBottomUp
                          ? tree.DumpToJsonBottomUp()
                          : tree.DumpToJsonTopDown();

                        //Debug.Assert(2 < json.Length,"expected valid non-empty JSON graph data");
                        //Debug.Print(json);

                        Console.WriteLine(json);

                        // Save this system hierarchy JSON in the
                        // appropriate domain specific collector.

                        json_collector[(int)GetMepDomain(system)].Add(json);

                        if (!parameters.StoreEntireJsonGraphOnProjectInfo)
                        {
                            Parameter p = system.get_Parameter(def);
                            p.Set(json);
                        }

                        nJsonBytes += json.Length;
                        ++nJsonGraphs;
                        ++nXmlFiles;
                    }
                    tree.CollectUniqueIds(sbs);
                }

                for (int i = 0; i < 3; ++i)
                {
                    if (sbs[i][sbs[i].Length - 1] == ',')
                    {
                        sbs[i].Remove(sbs[i].Length - 1, 1);
                    }
                    sbs[i].Append("]");
                }

                StringBuilder sb = new StringBuilder();

                sb.Append("{\"id\": 1 , \"name\" : \"MEP Systems\" , \"children\" : [{\"id\": 2 , \"name\": \"Mechanical System\",\"children\":");
                sb.Append(sbs[0].ToString());

                sb.Append("},{\"id\":3,\"name\":\"Electrical System\", \"children\":");
                sb.Append(sbs[1].ToString());

                sb.Append("},{\"id\":4,\"name\":\"Piping System\", \"children\":");
                sb.Append(sbs[2].ToString());
                sb.Append("}]}");

                StreamWriter file = new StreamWriter(
                  Path.ChangeExtension(
                    Path.Combine(outputFolder, "jsonData"),
                      "json"));

                file.WriteLine(sb.ToString());
                file.Flush();
                file.Close();

                t.Commit();
            }

            string msg = string.Format(
              "{0} XML files and {1} JSON graphs ({2} bytes) "
              + "generated in {3} ({4} total systems, {5} desirable):",
              nXmlFiles, nJsonGraphs, nJsonBytes,
              outputFolder, nAllSystems, nDesirableSystems);

            List<string> system_list = desirableSystems
              .Select<Element, string>(e =>
                string.Format("{0}({1})", e.Id, e.Name))
              .ToList<string>();

            system_list.Sort();

            string detail = string.Join(", ",
              system_list.ToArray<string>());

            //TaskDialog dlg = new TaskDialog(
            //  nXmlFiles.ToString() + " Systems");

            //dlg.MainInstruction = msg;
            //dlg.MainContent = detail;

            //dlg.Show();

            string[] json_systems = new string[3];
            int id = doc.Title.GetHashCode();

            for (MepDomain d = MepDomain.Mechanical;
              d < MepDomain.Count; ++d)
            {
                // Compare the systems using the label value,
                // which comes after the first comma.

                json_collector[(int)d].Sort((s, t)
                  => string.Compare(
                    s.Substring(s.IndexOf(",")),
                    t.Substring(t.IndexOf(","))));

                json_systems[(int)d]
                  = TreeNode.CreateJsonParentNode(
                    (++id).ToString(), d.ToString(),
                    json_collector[(int)d].ToArray<string>());
            }

            json = TreeNode.CreateJsonParentNode(
              doc.Title.GetHashCode().ToString(),
              doc.Title, json_systems);

            //Debug.Print(json);
            Console.WriteLine(json);


            if (parameters.StoreEntireJsonGraphOnProjectInfo)
            {
                using (Transaction t = new Transaction(doc))
                {
                    t.Start("Store MEP Graph Structure "
                      + "in JSON Shared Parameter");

                    Parameter p = json_storage_element
                    .get_Parameter(def);

                    p.Set(json);

                    t.Commit();
                }
            }

            return true;

        }
    }


    /// <summary>
    /// InputParams is used to parse the input Json parameters
    /// </summary>
    public class InputParams
    {
        //public bool Export { get; set; } = false;
        //public bool IncludeFireRating { get; set; } = true;
        //public bool IncludeComments { get; set; } = true;
        //public string inputElementId { get; set; } = "xxx";
        public bool StoreUniqueId { get; set; } = true;
        public bool StoreJsonGraphBottomUp { get; set; } = true;
        public bool StoreEntireJsonGraphOnProjectInfo { get; set; } = true;

        static public InputParams Parse(string jsonPath)
        {
            try
            {
                if (!File.Exists(jsonPath))
                    return new InputParams { StoreUniqueId = true, StoreJsonGraphBottomUp = true, StoreEntireJsonGraphOnProjectInfo = true };

                string jsonContents = File.ReadAllText(jsonPath);
                return JsonConvert.DeserializeObject<InputParams>(jsonContents);
            }
            catch (System.Exception ex)
            {
                Console.WriteLine("Exception when parsing json file: " + ex);
                return null;
            }
        }
    }

}
