#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// Replaces built-in Plane meshes (121 verts) on the selected objects with a shared
/// 4-vertex quad mesh authored in the Plane's own coordinate system — 10x10 on local
/// XZ, facing +Y, corner UVs/tangents copied from the Plane itself. Only the mesh
/// reference on the MeshFilter (and MeshCollider, if it used the Plane) changes, so
/// transform, materials, and prefab overrides are untouched.
/// Invoked from the Editools settings popup.
/// </summary>
static class PlaneToQuad
{
	const string k_MeshFolder = "Assets/Standard Assets/Editools/Runtime/PlaneToQuad";
	const string k_MeshAssetPath = k_MeshFolder + "/Plane Quad.asset";
	const string k_UndoName = "Replace Plane With Quad";

	internal static void ReplaceSelectedPlanes()
	{
		var planeMesh = Resources.GetBuiltinResource<Mesh>("New-Plane.fbx");
		Mesh quadMesh = null;
		int replaced = 0;

		foreach (var go in Selection.gameObjects)
		{
			var meshFilter = go.GetComponent<MeshFilter>();
			if (meshFilter == null || meshFilter.sharedMesh != planeMesh)
				continue;

			if (quadMesh == null)
				quadMesh = GetOrCreateQuadMesh(planeMesh);

			Undo.RecordObject(meshFilter, k_UndoName);
			meshFilter.sharedMesh = quadMesh;

			var meshCollider = go.GetComponent<MeshCollider>();
			if (meshCollider != null && meshCollider.sharedMesh == planeMesh)
			{
				Undo.RecordObject(meshCollider, k_UndoName);
				meshCollider.sharedMesh = quadMesh;
			}

			replaced++;
		}

		if (replaced == 0 && Selection.gameObjects.Length > 0)
			Debug.LogError("[Editools] Replace Plane With Quad: no built-in Plane mesh on the selected object(s)");
		else
			Debug.Log($"[Editools] Replace Plane With Quad: replaced {replaced} plane(s)");
	}

	/// <summary>
	/// Loads the shared quad mesh asset, generating it once from the built-in Plane's
	/// corner vertices so positions, UV orientation, and tangents match exactly.
	/// </summary>
	static Mesh GetOrCreateQuadMesh(Mesh planeMesh)
	{
		var existing = AssetDatabase.LoadAssetAtPath<Mesh>(k_MeshAssetPath);
		if (existing != null)
			return existing;

		var planeVerts = planeMesh.vertices;
		var planeUvs = planeMesh.uv;
		var planeTangents = planeMesh.tangents;

		// Corner slots keyed by local sign: 0=(-x,-z) 1=(-x,+z) 2=(+x,-z) 3=(+x,+z)
		var verts = new Vector3[4];
		var uvs = new Vector2[4];
		var tangents = new Vector4[4];
		float extent = planeMesh.bounds.extents.x;
		for (int i = 0; i < planeVerts.Length; i++)
		{
			var v = planeVerts[i];
			if (Mathf.Abs(v.x) < extent - 0.01f || Mathf.Abs(v.z) < extent - 0.01f)
				continue;
			int slot = (v.x < 0f ? 0 : 2) + (v.z < 0f ? 0 : 1);
			verts[slot] = v;
			uvs[slot] = planeUvs[i];
			tangents[slot] = planeTangents[i];
		}

		var mesh = new Mesh
		{
			name = "Plane Quad",
			vertices = verts,
			normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
			uv = uvs,
			tangents = tangents,
			// same winding as the Greyquad grid — faces local +Y
			triangles = new[] { 0, 1, 3, 0, 3, 2 }
		};
		mesh.RecalculateBounds();

		if (!AssetDatabase.IsValidFolder(k_MeshFolder))
			AssetDatabase.CreateFolder("Assets/Standard Assets/Editools/Runtime", "PlaneToQuad");
		AssetDatabase.CreateAsset(mesh, k_MeshAssetPath);
		AssetDatabase.SaveAssets();
		return mesh;
	}
}
#endif
