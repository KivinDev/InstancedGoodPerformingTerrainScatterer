using System.Collections.Generic;
using Sandbox;

public sealed partial class PerformantTerrainScatterer
{
	public enum ScattererNoiseType
	{
		None,
		Perlin,
		Simplex
	}

	public struct MaterialWeight
	{
		public MaterialWeight() { }
		[Property] public TerrainMaterial Material { get; set; } = null;
		[Property, Range( 0f, 100f )] public float Weight { get; set; } = 1f;
	}

	public struct ScattererModelEntry
	{
		public ScattererModelEntry() { }
		[Property, Category( "Base" )] public Model Model { get; set; } = null;
		[Property, Category( "Base" )] public float ZOffset { get; set; } = 0f;

		[Property, Category( "Noise Scattering" )]
		public ScattererNoiseType NoiseType { get; set; } = ScattererNoiseType.None;

		[Property, Category( "Noise Scattering" )]
		public float NoiseScale { get; set; } = 0.002f;

		[Property, Category( "Noise Scattering" ), Range( 0f, 1f )]
		public float NoiseThreshold { get; set; } = 0.4f;

		[Property, Category( "Performance" )] public float RenderDistance { get; set; } = 2500f;
		[Property, Category( "Terrain" )] public List<MaterialWeight> MaterialWeights { get; set; } = new();

		[Property, Category( "Randomization" )]
		public bool RandomizeScale { get; set; } = false;

		[Property, Category( "Randomization" ), ShowIf( nameof(RandomizeScale), true )]
		public float MinScale { get; set; } = 0.8f;

		[Property, Category( "Randomization" ), ShowIf( nameof(RandomizeScale), true )]
		public float MaxScale { get; set; } = 1.2f;

		[Property, Category( "LOD" ), Range( 0.1f, 4f )]
		public float LodBias { get; set; } = 1.0f;
	}

	private struct ChunkRenderData
	{
		public Vector3 Center;
		public ModelRenderBatch[] Batches;
	}

	private struct ModelRenderBatch
	{
		public Model Model;
		public Transform[] Transforms;
		public float RenderDistSq;
		public float[] LodDistancesSq;
		public int MaxLod;
	}

	private struct InstanceData
	{
		public Vector3 Position;
		public Rotation Rotation;
		public float Scale;
		public int ModelIndex;
	}

	private class ClutterChunk
	{
		public Vector3 Center;
		public readonly Dictionary<int, Transform[]> TransformsByModel = new();
	}
}
