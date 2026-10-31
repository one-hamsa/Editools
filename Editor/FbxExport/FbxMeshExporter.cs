#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Self-contained FBX exporter that bakes the CURRENT geometry of selected renderers
/// (posed for skinned meshes, shared mesh for static) into a single BINARY FBX 7.4 file.
///
/// Pure C# - no native DLL, no FBX SDK. Binary (not ASCII) because Blender's importer only reads
/// binary FBX. The document layout follows what the Autodesk FBX SDK writes (header extension with
/// SceneInfo, FileId / CreationTime / Creator records, GlobalSettings, Documents, References,
/// Definitions with property templates, Objects, Connections, Takes, and the SDK footer), so the
/// file opens in Blender, 3ds Max, Maya and re-imports into Unity.
/// Exports positions, normals, tangents, vertex colors and every populated UV channel.
///
/// Skinning and hierarchy are intentionally dropped: each renderer's geometry is baked into
/// WORLD space (so placement is preserved exactly, even for skinned meshes driven by far-away
/// bones), and every FBX node sits at identity. Units are meters (UnitScaleFactor 100).
///
/// Vertices are welded by position so the mesh imports as ONE connected object (Unity splits
/// verts at hard edges / UV seams); per-corner normals/UVs/colors preserve those seams. Adjacent
/// triangles can optionally be merged back into quads.
///
/// Coordinate convention matches Unity's own FBX exporter: X is mirrored (left-handed Unity ->
/// right-handed FBX) and triangle winding is flipped, so the result re-imports correctly and
/// reads correctly in DCC tools.
/// </summary>
static class FbxMeshExporter
{
	const uint FbxVersion = 7400;
	const string k_Creator = "Editools FbxMeshExporter";
	const string k_DialogTitle = "Bake Selection to FBX";

	// The FBX SDK derives FileId, CreationTime and the footer code from one timestamp and rejects
	// files where the three disagree. Blender's exporter ships this fixed, mutually consistent set.
	static readonly byte[] k_FileId =
	{
		0x28, 0xb3, 0x2a, 0xeb, 0xb6, 0x24, 0xcc, 0xc2, 0xbf, 0xc8, 0xb0, 0x2a, 0xa9, 0x2b, 0xfc, 0xf1
	};
	const string k_CreationTime = "1970-01-01 10:00:00:000";
	static readonly byte[] k_FooterCode =
	{
		0xfa, 0xbc, 0xab, 0x09, 0xd0, 0xc8, 0xd4, 0x66, 0xb1, 0x76, 0xfb, 0x83, 0x1c, 0xf7, 0x26, 0x7e
	};
	static readonly byte[] k_FooterMagic =
	{
		0xf8, 0x5a, 0x8c, 0x6a, 0xde, 0xf5, 0xd9, 0x7e, 0xec, 0xe9, 0x0c, 0xe3, 0x75, 0x05, 0x0a, 0x16
	};

	struct MeshEntry
	{
		public Mesh mesh;
		public string name;
		public Transform world;
		public bool owned; // baked snapshot we must DestroyImmediate after writing
	}

	struct QuadCandidate
	{
		public int ta, tb;
		public float score;
		public int[] qw, qo;
	}

	/// <summary>
	/// Exports every MeshRenderer / SkinnedMeshRenderer under the current selection to one FBX file
	/// chosen in a save dialog. <paramref name="quadMaxAngleDeg"/> is the largest face-normal angle
	/// between two triangles that still merges them into a quad.
	/// </summary>
	internal static void ExportSelection(bool exportQuads, float quadMaxAngleDeg)
	{
		var roots = Selection.gameObjects;
		if (roots == null || roots.Length == 0)
		{
			EditorUtility.DisplayDialog(k_DialogTitle,
				"Nothing selected. Select one or more objects in the Hierarchy, then try again.", "OK");
			return;
		}

		// Collect unique renderers across all selected hierarchies (includes inactive so nothing is silently dropped).
		var meshFilters = new List<MeshFilter>();
		var skinned = new List<SkinnedMeshRenderer>();
		var seen = new HashSet<Component>();

		foreach (var root in roots)
		{
			foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
			{
				// Legitimate filters: a MeshFilter with no mesh, or without a MeshRenderer, has nothing visible to export.
				if (mf.sharedMesh == null) continue;
				if (mf.GetComponent<MeshRenderer>() == null) continue;
				if (!mf.sharedMesh.isReadable)
				{
					Debug.LogError($"[FbxMeshExporter] Mesh '{mf.sharedMesh.name}' on '{mf.name}' is not Read/Write enabled - skipping. " +
						"Enable Read/Write in the model's import settings to export it.", mf);
					continue;
				}
				if (seen.Add(mf)) meshFilters.Add(mf);
			}

			foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			{
				if (smr.sharedMesh == null) continue; // legitimate filter: nothing to bake
				if (seen.Add(smr)) skinned.Add(smr);
			}
		}

		int total = meshFilters.Count + skinned.Count;
		if (total == 0)
		{
			EditorUtility.DisplayDialog(k_DialogTitle,
				"No exportable MeshRenderer or SkinnedMeshRenderer found under the selection.", "OK");
			return;
		}

		string defaultName = roots.Length == 1 ? SanitizeFileName(roots[0].name) : "BakedMeshes";
		string path = EditorUtility.SaveFilePanel(k_DialogTitle, "", defaultName + ".fbx", "fbx");
		if (string.IsNullOrEmpty(path)) return; // user cancelled

		// Build export entries. Skinned meshes are baked here (after the save dialog) to capture the current pose.
		var entries = new List<MeshEntry>(total);
		foreach (var mf in meshFilters)
			entries.Add(new MeshEntry { mesh = mf.sharedMesh, name = mf.name, world = mf.transform, owned = false });

		foreach (var smr in skinned)
		{
			var baked = new Mesh { name = smr.name + "_baked" };
			smr.BakeMesh(baked); // current pose, in the renderer's local space (no scale - localToWorldMatrix carries it)
			entries.Add(new MeshEntry { mesh = baked, name = smr.name, world = smr.transform, owned = true });
		}

		float quadMaxAngleCos = Mathf.Cos(quadMaxAngleDeg * Mathf.Deg2Rad);
		try
		{
			FbxNode root = BuildFbxTree(entries, exportQuads, quadMaxAngleCos, Path.GetFileName(path));
			byte[] bytes = SerializeBinary(root, FbxVersion);
			File.WriteAllBytes(path, bytes);
		}
		finally
		{
			foreach (var e in entries)
				if (e.owned) UnityEngine.Object.DestroyImmediate(e.mesh);
		}

		// If saved inside the project, import it so it shows up in the Project window.
		if (path.Replace('\\', '/').StartsWith(Application.dataPath))
		{
			string rel = "Assets" + path.Substring(Application.dataPath.Length);
			AssetDatabase.ImportAsset(rel.Replace('\\', '/'));
		}

		Debug.Log($"[FbxMeshExporter] Exported {entries.Count} mesh(es) ({(exportQuads ? "quads" : "triangles")}) to {path}");
		EditorUtility.RevealInFinder(path);
	}

	// ---------------------------------------------------------------------------------------------
	// FBX document tree
	// ---------------------------------------------------------------------------------------------

	static FbxNode BuildFbxTree(List<MeshEntry> entries, bool exportQuads, float quadMaxAngleCos, string fileName)
	{
		var root = new FbxNode("");

		long id = 1000000000L;
		long documentId = id++;
		var geomIds = new long[entries.Count];
		var modelIds = new long[entries.Count];
		for (int i = 0; i < entries.Count; i++)
		{
			geomIds[i] = id++;
			modelIds[i] = id++;
		}

		root.Child(BuildHeaderExtension(fileName));
		root.Child(new FbxNode("FileId").R(k_FileId));
		root.Child(new FbxNode("CreationTime").S(k_CreationTime));
		root.Child(new FbxNode("Creator").S(k_Creator));
		root.Child(BuildGlobalSettings());

		var docs = root.Child(new FbxNode("Documents"));
		docs.Child(new FbxNode("Count").I(1));
		var doc = docs.Child(new FbxNode("Document").L(documentId).S("").S("Scene"));
		var docProps = doc.Child(new FbxNode("Properties70"));
		docProps.Child(new FbxNode("P").S("SourceObject").S("object").S("").S(""));
		docProps.Child(P("ActiveAnimStackName", "KString", "", "", ""));
		doc.Child(new FbxNode("RootNode").L(0));

		root.Child(new FbxNode("References"));
		root.Child(BuildDefinitions(entries.Count));

		var objects = root.Child(new FbxNode("Objects"));
		for (int i = 0; i < entries.Count; i++)
			objects.Child(BuildGeometryNode(entries[i], geomIds[i], exportQuads, quadMaxAngleCos));
		for (int i = 0; i < entries.Count; i++)
			objects.Child(BuildModelNode(entries[i], modelIds[i]));

		var conns = root.Child(new FbxNode("Connections"));
		for (int i = 0; i < entries.Count; i++)
		{
			conns.Child(new FbxNode("C").S("OO").L(modelIds[i]).L(0));        // Model -> scene root
			conns.Child(new FbxNode("C").S("OO").L(geomIds[i]).L(modelIds[i])); // Geometry -> Model
		}

		root.Child(new FbxNode("Takes")).Child(new FbxNode("Current").S(""));

		return root;
	}

	static FbxNode BuildHeaderExtension(string fileName)
	{
		var header = new FbxNode("FBXHeaderExtension");
		header.Child(new FbxNode("FBXHeaderVersion").I(1003));
		header.Child(new FbxNode("FBXVersion").I((int)FbxVersion));
		header.Child(new FbxNode("EncryptionType").I(0));
		var now = DateTime.Now;
		var ts = header.Child(new FbxNode("CreationTimeStamp"));
		ts.Child(new FbxNode("Version").I(1000));
		ts.Child(new FbxNode("Year").I(now.Year));
		ts.Child(new FbxNode("Month").I(now.Month));
		ts.Child(new FbxNode("Day").I(now.Day));
		ts.Child(new FbxNode("Hour").I(now.Hour));
		ts.Child(new FbxNode("Minute").I(now.Minute));
		ts.Child(new FbxNode("Second").I(now.Second));
		ts.Child(new FbxNode("Millisecond").I(0));
		header.Child(new FbxNode("Creator").S(k_Creator));

		var info = header.Child(new FbxNode("SceneInfo").S("SceneInfo::GlobalInfo").S("UserData"));
		info.Child(new FbxNode("Type").S("UserData"));
		info.Child(new FbxNode("Version").I(100));
		var meta = info.Child(new FbxNode("MetaData"));
		meta.Child(new FbxNode("Version").I(100));
		foreach (string field in new[] { "Title", "Subject", "Author", "Keywords", "Revision", "Comment" })
			meta.Child(new FbxNode(field).S(""));
		string dateTime = now.ToUniversalTime().ToString("MM/dd/yyyy HH:mm:ss.fff");
		var props = info.Child(new FbxNode("Properties70"));
		props.Child(P("DocumentUrl", "KString", "Url", "", fileName));
		props.Child(P("SrcDocumentUrl", "KString", "Url", "", fileName));
		props.Child(new FbxNode("P").S("Original").S("Compound").S("").S(""));
		props.Child(P("Original|ApplicationVendor", "KString", "", "", "One Hamsa"));
		props.Child(P("Original|ApplicationName", "KString", "", "", k_Creator));
		props.Child(P("Original|ApplicationVersion", "KString", "", "", Application.unityVersion));
		props.Child(P("Original|DateTime_GMT", "DateTime", "", "", dateTime));
		props.Child(P("Original|FileName", "KString", "", "", fileName));
		props.Child(new FbxNode("P").S("LastSaved").S("Compound").S("").S(""));
		props.Child(P("LastSaved|ApplicationVendor", "KString", "", "", "One Hamsa"));
		props.Child(P("LastSaved|ApplicationName", "KString", "", "", k_Creator));
		props.Child(P("LastSaved|ApplicationVersion", "KString", "", "", Application.unityVersion));
		props.Child(P("LastSaved|DateTime_GMT", "DateTime", "", "", dateTime));
		return header;
	}

	static FbxNode BuildGlobalSettings()
	{
		var gs = new FbxNode("GlobalSettings");
		gs.Child(new FbxNode("Version").I(1000));
		var p = gs.Child(new FbxNode("Properties70"));
		p.Child(P("UpAxis", "int", "Integer", "", 1));
		p.Child(P("UpAxisSign", "int", "Integer", "", 1));
		p.Child(P("FrontAxis", "int", "Integer", "", 2));
		p.Child(P("FrontAxisSign", "int", "Integer", "", 1));
		p.Child(P("CoordAxis", "int", "Integer", "", 0));
		p.Child(P("CoordAxisSign", "int", "Integer", "", 1));
		p.Child(P("OriginalUpAxis", "int", "Integer", "", -1));
		p.Child(P("OriginalUpAxisSign", "int", "Integer", "", 1));
		// Centimeters per file unit: geometry is written in Unity meters.
		p.Child(P("UnitScaleFactor", "double", "Number", "", 100.0));
		p.Child(P("OriginalUnitScaleFactor", "double", "Number", "", 100.0));
		p.Child(P3("AmbientColor", "ColorRGB", "Color", "", 0, 0, 0));
		p.Child(P("DefaultCamera", "KString", "", "", "Producer Perspective"));
		p.Child(P("TimeMode", "enum", "", "", 11));
		p.Child(P("TimeSpanStart", "KTime", "Time", "", 0L));
		p.Child(P("TimeSpanStop", "KTime", "Time", "", 46186158000L));
		p.Child(P("CustomFrameRate", "double", "Number", "", 24.0));
		return gs;
	}

	static FbxNode BuildDefinitions(int meshCount)
	{
		var defs = new FbxNode("Definitions");
		defs.Child(new FbxNode("Version").I(100));
		defs.Child(new FbxNode("Count").I(1 + meshCount * 2));
		defs.Child(new FbxNode("ObjectType").S("GlobalSettings")).Child(new FbxNode("Count").I(1));

		var geometry = defs.Child(new FbxNode("ObjectType").S("Geometry"));
		geometry.Child(new FbxNode("Count").I(meshCount));
		var meshTemplate = geometry.Child(new FbxNode("PropertyTemplate").S("FbxMesh")).Child(new FbxNode("Properties70"));
		meshTemplate.Child(P3("Color", "ColorRGB", "Color", "", 0.8, 0.8, 0.8));
		meshTemplate.Child(P3("BBoxMin", "Vector3D", "Vector", "", 0, 0, 0));
		meshTemplate.Child(P3("BBoxMax", "Vector3D", "Vector", "", 0, 0, 0));
		meshTemplate.Child(P("Primary Visibility", "bool", "", "", 1));
		meshTemplate.Child(P("Casts Shadows", "bool", "", "", 1));
		meshTemplate.Child(P("Receive Shadows", "bool", "", "", 1));

		var model = defs.Child(new FbxNode("ObjectType").S("Model"));
		model.Child(new FbxNode("Count").I(meshCount));
		var nodeTemplate = model.Child(new FbxNode("PropertyTemplate").S("FbxNode")).Child(new FbxNode("Properties70"));
		nodeTemplate.Child(P("QuaternionInterpolate", "enum", "", "", 0));
		nodeTemplate.Child(P3("RotationOffset", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("RotationPivot", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("ScalingOffset", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("ScalingPivot", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P("TranslationActive", "bool", "", "", 0));
		nodeTemplate.Child(P3("TranslationMin", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("TranslationMax", "Vector3D", "Vector", "", 0, 0, 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("TranslationMin" + axis, "bool", "", "", 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("TranslationMax" + axis, "bool", "", "", 0));
		nodeTemplate.Child(P("RotationOrder", "enum", "", "", 0));
		nodeTemplate.Child(P("RotationSpaceForLimitOnly", "bool", "", "", 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("RotationStiffness" + axis, "double", "Number", "", 0.0));
		nodeTemplate.Child(P("AxisLen", "double", "Number", "", 10.0));
		nodeTemplate.Child(P3("PreRotation", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("PostRotation", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P("RotationActive", "bool", "", "", 0));
		nodeTemplate.Child(P3("RotationMin", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("RotationMax", "Vector3D", "Vector", "", 0, 0, 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("RotationMin" + axis, "bool", "", "", 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("RotationMax" + axis, "bool", "", "", 0));
		nodeTemplate.Child(P("InheritType", "enum", "", "", 0));
		nodeTemplate.Child(P("ScalingActive", "bool", "", "", 0));
		nodeTemplate.Child(P3("ScalingMin", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("ScalingMax", "Vector3D", "Vector", "", 1, 1, 1));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("ScalingMin" + axis, "bool", "", "", 0));
		foreach (string axis in new[] { "X", "Y", "Z" })
			nodeTemplate.Child(P("ScalingMax" + axis, "bool", "", "", 0));
		nodeTemplate.Child(P3("GeometricTranslation", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("GeometricRotation", "Vector3D", "Vector", "", 0, 0, 0));
		nodeTemplate.Child(P3("GeometricScaling", "Vector3D", "Vector", "", 1, 1, 1));
		foreach (string prefix in new[] { "MinDampRange", "MaxDampRange", "MinDampStrength", "MaxDampStrength", "PreferedAngle" })
			foreach (string axis in new[] { "X", "Y", "Z" })
				nodeTemplate.Child(P(prefix + axis, "double", "Number", "", 0.0));
		nodeTemplate.Child(new FbxNode("P").S("LookAtProperty").S("object").S("").S(""));
		nodeTemplate.Child(new FbxNode("P").S("UpVectorProperty").S("object").S("").S(""));
		nodeTemplate.Child(P("Show", "bool", "", "", 1));
		nodeTemplate.Child(P("NegativePercentShapeSupport", "bool", "", "", 1));
		nodeTemplate.Child(P("DefaultAttributeIndex", "int", "Integer", "", -1));
		nodeTemplate.Child(P("Freeze", "bool", "", "", 0));
		nodeTemplate.Child(P("LODBox", "bool", "", "", 0));
		nodeTemplate.Child(P3("Lcl Translation", "Lcl Translation", "", "A", 0, 0, 0));
		nodeTemplate.Child(P3("Lcl Rotation", "Lcl Rotation", "", "A", 0, 0, 0));
		nodeTemplate.Child(P3("Lcl Scaling", "Lcl Scaling", "", "A", 1, 1, 1));
		nodeTemplate.Child(P("Visibility", "Visibility", "", "A", 1.0));
		nodeTemplate.Child(P("Visibility Inheritance", "Visibility Inheritance", "", "", 1));
		return defs;
	}

	static FbxNode P(string name, string type, string sub, string flags, object value)
	{
		var n = new FbxNode("P").S(name).S(type).S(sub).S(flags);
		switch (value)
		{
			case int iv: n.I(iv); break;
			case long lv: n.L(lv); break;
			case double dv: n.D(dv); break;
			case string sv: n.S(sv); break;
			default: throw new ArgumentException($"Unsupported P value type {value?.GetType()}");
		}
		return n;
	}

	static FbxNode P3(string name, string type, string sub, string flags, double x, double y, double z) =>
		new FbxNode("P").S(name).S(type).S(sub).S(flags).D(x).D(y).D(z);

	static FbxNode BuildModelNode(MeshEntry e, long modelId)
	{
		// Geometry is already baked in world space, so the node itself stays at identity.
		var model = new FbxNode("Model").L(modelId).S($"Model::{e.name}").S("Mesh");
		model.Child(new FbxNode("Version").I(232));
		var props = model.Child(new FbxNode("Properties70"));
		props.Child(P3("Lcl Translation", "Lcl Translation", "", "A", 0, 0, 0));
		props.Child(P3("Lcl Rotation", "Lcl Rotation", "", "A", 0, 0, 0));
		props.Child(P3("Lcl Scaling", "Lcl Scaling", "", "A", 1, 1, 1));
		props.Child(P("DefaultAttributeIndex", "int", "Integer", "", 0));
		props.Child(P("InheritType", "enum", "", "", 1));
		model.Child(new FbxNode("MultiLayer").I(0));
		model.Child(new FbxNode("MultiTake").I(0));
		model.Child(new FbxNode("Shading").C(true));
		model.Child(new FbxNode("Culling").S("CullingOff"));
		return model;
	}

	static FbxNode BuildGeometryNode(MeshEntry e, long geomId, bool exportQuads, float quadMaxAngleCos)
	{
		Mesh m = e.mesh;
		var localVerts = m.vertices;
		var srcNormals = m.normals;
		var srcTangents = m.tangents;
		var colors = m.colors;

		// Bake into world space (exact for any hierarchy - sidesteps lossyScale/Euler node decomposition,
		// which is what offset skinned meshes driven by far-away bones). Then mirror X to convert Unity's
		// left-handed space into FBX's right-handed space, matching Unity's own FBX exporter.
		Matrix4x4 toWorld = e.world.localToWorldMatrix;
		Matrix4x4 toWorldNormal = toWorld.inverse.transpose;

		var worldVerts = new Vector3[localVerts.Length];
		for (int i = 0; i < localVerts.Length; i++)
		{
			Vector3 p = toWorld.MultiplyPoint3x4(localVerts[i]);
			p.x = -p.x;
			worldVerts[i] = p;
		}

		bool hasNormals = srcNormals.Length == localVerts.Length;
		var worldNormals = hasNormals ? new Vector3[srcNormals.Length] : Array.Empty<Vector3>();
		for (int i = 0; i < worldNormals.Length; i++)
		{
			Vector3 n = toWorldNormal.MultiplyVector(srcNormals[i]).normalized;
			n.x = -n.x;
			worldNormals[i] = n;
		}

		bool hasTangents = srcTangents.Length == localVerts.Length;
		var worldTangents = hasTangents ? new Vector4[srcTangents.Length] : Array.Empty<Vector4>();
		for (int i = 0; i < worldTangents.Length; i++)
		{
			Vector3 t = toWorld.MultiplyVector(srcTangents[i]).normalized;
			worldTangents[i] = new Vector4(-t.x, t.y, t.z, srcTangents[i].w);
		}

		bool hasColors = colors.Length == localVerts.Length;

		// Weld duplicate vertices (Unity splits verts at hard edges / UV seams with identical positions);
		// welding reconnects the topology so the mesh imports as ONE object, while the per-corner attributes
		// written below preserve the seams.
		var weldMap = new int[localVerts.Length];
		var weldedPositions = new List<Vector3>(localVerts.Length);
		var posToWelded = new Dictionary<Vector3, int>(localVerts.Length);
		for (int i = 0; i < localVerts.Length; i++)
		{
			Vector3 key = localVerts[i];
			if (!posToWelded.TryGetValue(key, out int w))
			{
				w = weldedPositions.Count;
				weldedPositions.Add(worldVerts[i]);
				posToWelded[key] = w;
			}
			weldMap[i] = w;
		}

		// Triangles as (welded, original) corner pairs, winding flipped for the X-mirror.
		var triW = new List<int[]>();
		var triO = new List<int[]>();
		for (int s = 0; s < m.subMeshCount; s++)
		{
			if (m.GetTopology(s) != MeshTopology.Triangles)
			{
				Debug.LogError($"[FbxMeshExporter] Submesh {s} of '{m.name}' is not triangles - skipped.", m);
				continue;
			}
			var tris = m.GetTriangles(s);
			for (int t = 0; t < tris.Length; t += 3)
			{
				int a = tris[t], b = tris[t + 1], c = tris[t + 2];
				triW.Add(new[] { weldMap[a], weldMap[c], weldMap[b] });
				triO.Add(new[] { a, c, b });
			}
		}

		List<int[]> polyW, polyO;
		if (exportQuads)
			MergeToQuads(triW, triO, weldedPositions, quadMaxAngleCos, out polyW, out polyO);
		else
		{
			polyW = triW;
			polyO = triO;
		}

		// Flat original-corner list (polygon order) for the per-corner attribute layers.
		var cornerOriginal = new List<int>();
		foreach (var po in polyO) cornerOriginal.AddRange(po);
		int cornerCount = cornerOriginal.Count;

		// Vertices (welded world positions, flattened).
		var vertsFlat = new double[weldedPositions.Count * 3];
		for (int i = 0; i < weldedPositions.Count; i++)
		{
			Vector3 v = weldedPositions[i];
			vertsFlat[i * 3] = v.x;
			vertsFlat[i * 3 + 1] = v.y;
			vertsFlat[i * 3 + 2] = v.z;
		}

		// PolygonVertexIndex (welded indices, last corner of each polygon negated).
		var polyIndex = new int[cornerCount];
		int ci = 0;
		foreach (var poly in polyW)
			for (int k = 0; k < poly.Length; k++)
				polyIndex[ci++] = (k == poly.Length - 1) ? -(poly[k] + 1) : poly[k];

		// UV channels (per original vertex).
		var uvChannels = new List<KeyValuePair<int, Vector2[]>>();
		var uvTemp = new List<Vector2>();
		for (int ch = 0; ch < 8; ch++)
		{
			m.GetUVs(ch, uvTemp);
			if (uvTemp.Count == localVerts.Length)
				uvChannels.Add(new KeyValuePair<int, Vector2[]>(ch, uvTemp.ToArray()));
		}

		// ----- assemble Geometry node -----
		var geo = new FbxNode("Geometry").L(geomId).S($"Geometry::{e.name}").S("Mesh");
		geo.Child(new FbxNode("Properties70"));
		geo.Child(new FbxNode("GeometryVersion").I(124));
		geo.Child(new FbxNode("Vertices").ArrD(vertsFlat));
		geo.Child(new FbxNode("PolygonVertexIndex").ArrI(polyIndex));

		if (hasNormals)
		{
			var normalsFlat = new double[cornerCount * 3];
			for (int i = 0; i < cornerCount; i++)
			{
				Vector3 n = worldNormals[cornerOriginal[i]];
				normalsFlat[i * 3] = n.x;
				normalsFlat[i * 3 + 1] = n.y;
				normalsFlat[i * 3 + 2] = n.z;
			}
			var le = new FbxNode("LayerElementNormal").I(0);
			le.Child(new FbxNode("Version").I(102));
			le.Child(new FbxNode("Name").S(""));
			le.Child(new FbxNode("MappingInformationType").S("ByPolygonVertex"));
			le.Child(new FbxNode("ReferenceInformationType").S("Direct"));
			le.Child(new FbxNode("Normals").ArrD(normalsFlat));
			geo.Child(le);
		}

		if (hasTangents)
		{
			var tangentsFlat = new double[cornerCount * 3];
			for (int i = 0; i < cornerCount; i++)
			{
				Vector4 t = worldTangents[cornerOriginal[i]];
				tangentsFlat[i * 3] = t.x;
				tangentsFlat[i * 3 + 1] = t.y;
				tangentsFlat[i * 3 + 2] = t.z;
			}
			var le = new FbxNode("LayerElementTangent").I(0);
			le.Child(new FbxNode("Version").I(102));
			le.Child(new FbxNode("Name").S("tangents"));
			le.Child(new FbxNode("MappingInformationType").S("ByPolygonVertex"));
			le.Child(new FbxNode("ReferenceInformationType").S("Direct"));
			le.Child(new FbxNode("Tangents").ArrD(tangentsFlat));
			geo.Child(le);
		}

		if (hasColors)
		{
			var colorsFlat = new double[cornerCount * 4];
			for (int i = 0; i < cornerCount; i++)
			{
				Color c = colors[cornerOriginal[i]];
				colorsFlat[i * 4] = c.r;
				colorsFlat[i * 4 + 1] = c.g;
				colorsFlat[i * 4 + 2] = c.b;
				colorsFlat[i * 4 + 3] = c.a;
			}
			var le = new FbxNode("LayerElementColor").I(0);
			le.Child(new FbxNode("Version").I(101));
			le.Child(new FbxNode("Name").S("colors"));
			le.Child(new FbxNode("MappingInformationType").S("ByPolygonVertex"));
			le.Child(new FbxNode("ReferenceInformationType").S("Direct"));
			le.Child(new FbxNode("Colors").ArrD(colorsFlat));
			geo.Child(le);
		}

		for (int u = 0; u < uvChannels.Count; u++)
		{
			int ch = uvChannels[u].Key;
			Vector2[] uvs = uvChannels[u].Value;
			var uvFlat = new double[cornerCount * 2];
			for (int i = 0; i < cornerCount; i++)
			{
				Vector2 uv = uvs[cornerOriginal[i]];
				uvFlat[i * 2] = uv.x;
				uvFlat[i * 2 + 1] = uv.y;
			}
			var le = new FbxNode("LayerElementUV").I(u);
			le.Child(new FbxNode("Version").I(101));
			le.Child(new FbxNode("Name").S($"UV{ch}"));
			le.Child(new FbxNode("MappingInformationType").S("ByPolygonVertex"));
			le.Child(new FbxNode("ReferenceInformationType").S("Direct"));
			le.Child(new FbxNode("UV").ArrD(uvFlat));
			geo.Child(le);
		}

		// Layer 0 holds normals/tangents/colors and the first UV set.
		var layer0 = new FbxNode("Layer").I(0);
		layer0.Child(new FbxNode("Version").I(100));
		if (hasNormals) layer0.Child(LayerElementRef("LayerElementNormal", 0));
		if (hasTangents) layer0.Child(LayerElementRef("LayerElementTangent", 0));
		if (hasColors) layer0.Child(LayerElementRef("LayerElementColor", 0));
		if (uvChannels.Count > 0) layer0.Child(LayerElementRef("LayerElementUV", 0));
		geo.Child(layer0);

		// Additional UV sets each get their own layer.
		for (int u = 1; u < uvChannels.Count; u++)
		{
			var layer = new FbxNode("Layer").I(u);
			layer.Child(new FbxNode("Version").I(100));
			layer.Child(LayerElementRef("LayerElementUV", u));
			geo.Child(layer);
		}

		return geo;
	}

	static FbxNode LayerElementRef(string type, int typedIndex)
	{
		var le = new FbxNode("LayerElement");
		le.Child(new FbxNode("Type").S(type));
		le.Child(new FbxNode("TypedIndex").I(typedIndex));
		return le;
	}

	// ---------------------------------------------------------------------------------------------
	// Binary FBX serialization
	// ---------------------------------------------------------------------------------------------

	static byte[] SerializeBinary(FbxNode root, uint version)
	{
		using var ms = new MemoryStream(1 << 20);
		using var w = new BinaryWriter(ms, Encoding.UTF8, true);

		// Header: "Kaydara FBX Binary  " + 0x00 0x1A 0x00 + uint32 version.
		w.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  "));
		w.Write((byte)0x00);
		w.Write((byte)0x1A);
		w.Write((byte)0x00);
		w.Write(version);

		foreach (var child in root.Children)
			WriteNode(w, child);
		w.Write(new byte[13]); // null record terminates the top-level list (13 bytes for ver < 7500)

		// Footer, in the SDK's layout: footer code, 4 zero bytes, zero padding up to the next 16-byte
		// boundary (a full 16 when already aligned), version, 120 zero bytes, fixed magic.
		w.Write(k_FooterCode);
		w.Write((uint)0);
		long pos = ms.Position;
		int pad = (int)((16 - (pos % 16)) % 16);
		if (pad == 0) pad = 16;
		w.Write(new byte[pad]);
		w.Write(version);
		w.Write(new byte[120]);
		w.Write(k_FooterMagic);

		w.Flush();
		return ms.ToArray();
	}

	static void WriteNode(BinaryWriter w, FbxNode node)
	{
		Stream s = w.BaseStream;
		long startPos = s.Position;

		w.Write((uint)0);                  // EndOffset (patched below)
		w.Write((uint)node.Props.Count);   // NumProperties
		long propLenPos = s.Position;
		w.Write((uint)0);                  // PropertyListLen (patched below)
		byte[] nameBytes = Encoding.UTF8.GetBytes(node.Name);
		w.Write((byte)nameBytes.Length);
		w.Write(nameBytes);

		long propStart = s.Position;
		foreach (var p in node.Props) WriteProp(w, p);
		uint propLen = (uint)(s.Position - propStart);

		foreach (var child in node.Children) WriteNode(w, child);
		// A nested list ends with a null record; a node with neither children nor properties carries one too.
		if (node.Children.Count > 0 || node.Props.Count == 0)
			w.Write(new byte[13]);

		long endPos = s.Position;
		s.Position = startPos;
		w.Write((uint)endPos);
		s.Position = propLenPos;
		w.Write(propLen);
		s.Position = endPos;
	}

	static void WriteProp(BinaryWriter w, FbxProp p)
	{
		switch (p.Type)
		{
			case PType.C:
				w.Write((byte)'C');
				w.Write((byte)((bool)p.Value ? 1 : 0));
				break;
			case PType.I:
				w.Write((byte)'I');
				w.Write((int)p.Value);
				break;
			case PType.L:
				w.Write((byte)'L');
				w.Write((long)p.Value);
				break;
			case PType.D:
				w.Write((byte)'D');
				w.Write((double)p.Value);
				break;
			case PType.S:
				byte[] str = Encoding.UTF8.GetBytes((string)p.Value);
				w.Write((byte)'S');
				w.Write((uint)str.Length);
				w.Write(str);
				break;
			case PType.R:
				var raw = (byte[])p.Value;
				w.Write((byte)'R');
				w.Write((uint)raw.Length);
				w.Write(raw);
				break;
			case PType.ArrD:
				var arrD = (double[])p.Value;
				w.Write((byte)'d');
				w.Write((uint)arrD.Length);
				w.Write((uint)0);                    // encoding: 0 = raw (uncompressed)
				w.Write((uint)(arrD.Length * 8));     // byte length
				foreach (double d in arrD) w.Write(d);
				break;
			case PType.ArrI:
				var arrI = (int[])p.Value;
				w.Write((byte)'i');
				w.Write((uint)arrI.Length);
				w.Write((uint)0);
				w.Write((uint)(arrI.Length * 4));
				foreach (int v in arrI) w.Write(v);
				break;
			default:
				throw new ArgumentException($"Unsupported FBX property type {p.Type}");
		}
	}

	// ---------------------------------------------------------------------------------------------
	// Triangle -> quad reconstruction
	// ---------------------------------------------------------------------------------------------

	static void MergeToQuads(List<int[]> triW, List<int[]> triO, List<Vector3> pos, float maxAngleCos,
		out List<int[]> polyW, out List<int[]> polyO)
	{
		int n = triW.Count;

		// Map each shared edge to the triangles touching it.
		var edgeToTri = new Dictionary<long, List<int>>(n * 2);
		for (int ti = 0; ti < n; ti++)
		{
			var w = triW[ti];
			AddEdge(edgeToTri, w[0], w[1], ti);
			AddEdge(edgeToTri, w[1], w[2], ti);
			AddEdge(edgeToTri, w[2], w[0], ti);
		}

		// Score every mergeable triangle pair, then greedily accept the best (each triangle used once).
		var candidates = new List<QuadCandidate>();
		foreach (var kv in edgeToTri)
		{
			if (kv.Value.Count != 2) continue;
			int ta = kv.Value[0], tb = kv.Value[1];
			if (TryBuildQuad(triW[ta], triO[ta], triW[tb], triO[tb], kv.Key, pos, maxAngleCos, out var qw, out var qo, out float score))
				candidates.Add(new QuadCandidate { ta = ta, tb = tb, score = score, qw = qw, qo = qo });
		}
		candidates.Sort((x, y) => y.score.CompareTo(x.score));

		var used = new bool[n];
		polyW = new List<int[]>();
		polyO = new List<int[]>();
		foreach (var c in candidates)
		{
			if (used[c.ta] || used[c.tb]) continue;
			used[c.ta] = true;
			used[c.tb] = true;
			polyW.Add(c.qw);
			polyO.Add(c.qo);
		}
		for (int ti = 0; ti < n; ti++)
			if (!used[ti])
			{
				polyW.Add(triW[ti]);
				polyO.Add(triO[ti]);
			}
	}

	static void AddEdge(Dictionary<long, List<int>> map, int a, int b, int tri)
	{
		long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
		if (!map.TryGetValue(key, out var list))
		{
			list = new List<int>(2);
			map[key] = list;
		}
		list.Add(tri);
	}

	static bool TryBuildQuad(int[] aw, int[] ao, int[] bw, int[] bo, long edgeKey, List<Vector3> pos, float maxAngleCos,
		out int[] quadW, out int[] quadO, out float score)
	{
		quadW = null;
		quadO = null;
		score = 0f;

		int u = (int)(edgeKey >> 32);
		int v = (int)(edgeKey & 0xffffffff);

		// Find the shared edge inside triangle A.
		int i = -1;
		for (int k = 0; k < 3; k++)
		{
			int w0 = aw[k], w1 = aw[(k + 1) % 3];
			if ((w0 == u && w1 == v) || (w0 == v && w1 == u)) { i = k; break; }
		}
		if (i < 0) return false;

		int s0 = aw[i], s1 = aw[(i + 1) % 3], c = aw[(i + 2) % 3];
		int so0 = ao[i], so1 = ao[(i + 1) % 3], co = ao[(i + 2) % 3];

		// Find B's opposite corner, and require B's shared corners to carry the same original (no seam on the diagonal).
		int xw = -1, xo = -1, matchS0 = -1, matchS1 = -1;
		for (int k = 0; k < 3; k++)
		{
			int w = bw[k];
			if (w == s0) matchS0 = bo[k];
			else if (w == s1) matchS1 = bo[k];
			else { xw = w; xo = bo[k]; }
		}
		if (xw < 0) return false;
		if (matchS0 != so0 || matchS1 != so1) return false; // seam along the diagonal - keep as triangles

		quadW = new[] { s0, xw, s1, c };
		quadO = new[] { so0, xo, so1, co };

		if (!IsConvexPlanarQuad(pos[s0], pos[xw], pos[s1], pos[c], maxAngleCos, out score))
		{
			quadW = null;
			quadO = null;
			return false;
		}
		return true;
	}

	static bool IsConvexPlanarQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float maxAngleCos, out float planarity)
	{
		planarity = 0f;
		Vector3 n1 = Vector3.Cross(p1 - p0, p2 - p0);
		Vector3 n2 = Vector3.Cross(p2 - p0, p3 - p0);
		float m1 = n1.magnitude, m2 = n2.magnitude;
		if (m1 < 1e-12f || m2 < 1e-12f) return false; // degenerate triangle
		n1 /= m1;
		n2 /= m2;
		planarity = Vector3.Dot(n1, n2);
		if (planarity < maxAngleCos) return false;

		Vector3 n = (n1 + n2).normalized;
		var q = new[] { p0, p1, p2, p3 };
		for (int k = 0; k < 4; k++)
		{
			Vector3 e1 = q[(k + 1) % 4] - q[k];
			Vector3 e2 = q[(k + 2) % 4] - q[(k + 1) % 4];
			if (Vector3.Dot(Vector3.Cross(e1, e2), n) <= 0f) return false; // reflex corner -> not convex
		}
		return true;
	}

	static string SanitizeFileName(string name)
	{
		foreach (char ch in Path.GetInvalidFileNameChars())
			name = name.Replace(ch, '_');
		return name;
	}

	// ---------------------------------------------------------------------------------------------
	// Minimal FBX node/property model
	// ---------------------------------------------------------------------------------------------

	enum PType { C, I, L, D, S, R, ArrD, ArrI }

	struct FbxProp
	{
		public PType Type;
		public object Value;
		public FbxProp(PType type, object value) { Type = type; Value = value; }
	}

	class FbxNode
	{
		public readonly string Name;
		public readonly List<FbxProp> Props = new();
		public readonly List<FbxNode> Children = new();

		public FbxNode(string name) { Name = name; }

		public FbxNode C(bool v) { Props.Add(new FbxProp(PType.C, v)); return this; }
		public FbxNode I(int v) { Props.Add(new FbxProp(PType.I, v)); return this; }
		public FbxNode L(long v) { Props.Add(new FbxProp(PType.L, v)); return this; }
		public FbxNode D(double v) { Props.Add(new FbxProp(PType.D, v)); return this; }
		public FbxNode S(string v) { Props.Add(new FbxProp(PType.S, v)); return this; }
		public FbxNode R(byte[] v) { Props.Add(new FbxProp(PType.R, v)); return this; }
		public FbxNode ArrD(double[] v) { Props.Add(new FbxProp(PType.ArrD, v)); return this; }
		public FbxNode ArrI(int[] v) { Props.Add(new FbxProp(PType.ArrI, v)); return this; }

		public FbxNode Child(FbxNode c) { Children.Add(c); return c; }
	}
}
#endif
