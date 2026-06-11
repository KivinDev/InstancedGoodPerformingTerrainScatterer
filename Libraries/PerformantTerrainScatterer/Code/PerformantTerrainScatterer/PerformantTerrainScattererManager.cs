using System;
using System.Collections.Generic;
using Sandbox;

public sealed partial class PerformantTerrainScatterer : Component, Component.ExecuteInEditor
{
	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) )] public Terrain TargetTerrain { get; set; }
	[Property, Category( "Models" ), Description( "Click ``Reinitialize``, after changing properties." ), Change( nameof( InitializeSystem ) )] public List<ScattererModelEntry> Models { get; set; } = new();
	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) ), Range( 0, 90 )] public float MaxSlopeAngle { get; set; } = 45f;
	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) )] public bool ReduceDensityOnTransitions { get; set; } = false;
	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) ), ShowIf( nameof( ReduceDensityOnTransitions ), true )] public float TransitionThinningIntensity { get; set; } = 1.0f;
	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) )] public bool SnapToGround { get; set; } = true;
	[Property, Category( "Collision" ), Change( nameof( InitializeSystem ) )] public bool CheckObstructions { get; set; } = true;
	[Property, Category( "Collision" ), Change( nameof( InitializeSystem ) )] public float TraceStartHeight { get; set; } = 200f;
	[Property, Category( "Collision" ), Change( nameof( InitializeSystem ) )] public float GridCellSize { get; set; } = 50f;
	[Property, Category( "Collision" ), Change( nameof( InitializeSystem ) )] public int ObstructionBuffer { get; set; } = 2;
	[Property, Category( "Collision" )] public string[] TraceIgnoreTags { get; set; } = { "player", "trigger", "clutter" };
	[Property, Category( "Performance" ), Change( nameof( InitializeSystem ) )] public int MaxInstancesPerChunk { get; set; } = 500;
	[Property, Category( "Performance" ), Change( nameof( InitializeSystem ) )] public float ChunkSize { get; set; } = 500f;
	[Property, Category( "Performance" ), Change( nameof( InitializeSystem ) )] public float ChunkLoadDistance { get; set; } = 3000f;

	[Property, Category( "Performance" )] public bool UseFrustumCulling { get; set; } = true;
	[Property, Category( "Performance" )] public float FrustumCullMinDistance { get; set; } = 800f;

	[Property, Category( "Terrain" ), Change( nameof( InitializeSystem ) )] public bool UseTerrainNormal { get; set; } = true;
	[Property, Category( "Seed" ), Change( nameof( InitializeSystem ) )] public int Seed { get; set; } = 12345;
	[Property, Category( "Randomization" ), Change( nameof( InitializeSystem ) )] public bool RandomizeRotation { get; set; } = true;

	private readonly Dictionary<(int, int), ClutterChunk> _activeChunks = new();
	private readonly HashSet<(int, int)> _generatingChunks = new();
	private readonly List<(int, int)> _chunkRemovalList = new();

	private ushort[] _heightMapPixels;
	private uint[] _controlMapPixels;
	private int _resolution;
	private float _terrainSize;
	private float _terrainHeight;
	private float _maxRenderDistance;
	private bool[][] _allowedModelIndices;
	private GameObject _cachedHitObject;
	private bool _cachedIsObstruction;
	private Sandbox.Utility.INoiseField[] _modelNoiseFields;
	private float _totalWeight;
	private Vector3 _lastCameraPos;
	private const float CameraUpdateThresholdSq = 10000f;
	private const float UshortMaxReciprocal = 1.0f / ushort.MaxValue;
	private SceneCustomObject _sceneObject;

	private ScattererModelEntry[] ModelEntries { get; set; }
	private float[] _modelRenderDistSq;
	private int[] _modelLodCounts;
	private float[][] _modelLodDistancesSq;

	private ChunkRenderData[] _renderCache = Array.Empty<ChunkRenderData>();

	[ThreadStatic]
	private static List<Transform>[] _lodBuckets;

	protected override void OnEnabled()
	{
		if ( !TargetTerrain.IsValid() )
		{
			TargetTerrain = Scene?.GetComponentInChildren<Terrain>();
		}

		CreateSceneObject();
		InitializeSystem();
	}

	protected override void OnUpdate()
	{
		ManageChunks();
	}

	protected override void OnDisabled()
	{
		_sceneObject?.Delete();
		_sceneObject = null;
	}

	private void CreateSceneObject()
	{
		_sceneObject?.Delete();
		if ( Scene == null ) return;

		_sceneObject = new SceneCustomObject( Scene.SceneWorld )
		{
			RenderOverride = RenderClutter,
			Transform = new Transform( Vector3.Zero, Rotation.Identity, 1f ),
			Flags = { IsOpaque = true, IsTranslucent = false }
		};
	}

	[Button( "Reinitialize" )]
	public void InitializeSystem()
	{
		_activeChunks.Clear();
		_generatingChunks.Clear();
		_chunkRemovalList.Clear();
		_renderCache = Array.Empty<ChunkRenderData>();
		ModelEntries = null;
		_modelRenderDistSq = null;
		_modelLodCounts = null;
		_modelLodDistancesSq = null;

		if ( Models == null || Models.Count == 0 ) return;
		if ( !TargetTerrain.IsValid() || TargetTerrain.Storage == null ) return;

		_totalWeight = 0f;
		_maxRenderDistance = ChunkLoadDistance;

		foreach ( var t in Models )
		{
			if ( t.Model != null )
			{
				_totalWeight += t.Weight;
				if ( t.RenderDistance > _maxRenderDistance )
					_maxRenderDistance = t.RenderDistance;
			}
		}

		if ( _totalWeight <= 0f ) return;

		ModelEntries = Models.ToArray();
		_modelRenderDistSq = new float[ModelEntries.Length];
		_modelLodCounts = new int[ModelEntries.Length];
		_modelLodDistancesSq = new float[ModelEntries.Length][];

		for ( int m = 0; m < ModelEntries.Length; m++ )
		{
			_modelRenderDistSq[m] = ModelEntries[m].RenderDistance * ModelEntries[m].RenderDistance;
			var mdl = ModelEntries[m].Model;

			if ( mdl != null )
			{
				_modelLodCounts[m] = Math.Max( 1, mdl.MeshInfo.LodCount );
				if ( mdl.MeshInfo.LodSwitchDistances != null && mdl.MeshInfo.LodSwitchDistances.Length > 0 && mdl.MeshInfo.LodSwitchDistances[0] > 0f )
				{
					var switchDistances = mdl.MeshInfo.LodSwitchDistances;
					_modelLodDistancesSq[m] = new float[switchDistances.Length];
					for ( int i = 0; i < switchDistances.Length; i++ )
					{
						float dist = switchDistances[i] * ModelEntries[m].LodBias;
						_modelLodDistancesSq[m][i] = dist * dist;
					}
				}
				else if ( _modelLodCounts[m] > 1 )
				{
					int switchCount = _modelLodCounts[m] - 1;
					_modelLodDistancesSq[m] = new float[switchCount];
					float step = ModelEntries[m].RenderDistance / _modelLodCounts[m];
					for ( int i = 0; i < switchCount; i++ )
					{
						float dist = (step * (i + 1)) * ModelEntries[m].LodBias;
						_modelLodDistancesSq[m][i] = dist * dist;
					}
				}
				else
				{
					_modelLodDistancesSq[m] = Array.Empty<float>();
				}
			}
			else
			{
				_modelLodCounts[m] = 1;
				_modelLodDistancesSq[m] = Array.Empty<float>();
			}
		}

		_heightMapPixels = TargetTerrain.Storage.HeightMap;
		_controlMapPixels = TargetTerrain.Storage.ControlMap;
		_resolution = TargetTerrain.Storage.Resolution;
		_terrainSize = TargetTerrain.TerrainSize;
		_terrainHeight = TargetTerrain.TerrainHeight;
		_allowedModelIndices = new bool[Models.Count][];

		for ( int m = 0; m < Models.Count; m++ )
		{
			_allowedModelIndices[m] = new bool[32];
			if ( TargetTerrain.Storage.Materials != null && Models[m].AllowedMaterials != null )
			{
				for ( int i = 0; i < TargetTerrain.Storage.Materials.Count; i++ )
				{
					if ( Models[m].AllowedMaterials.Contains( TargetTerrain.Storage.Materials[i] ) )
						_allowedModelIndices[m][i] = true;
				}
			}
		}

		_modelNoiseFields = new Sandbox.Utility.INoiseField[Models.Count];
		for ( int m = 0; m < Models.Count; m++ )
		{
			var modelEntry = Models[m];
			if ( modelEntry.NoiseType == ScattererNoiseType.None ) continue;
			var parameters = new Sandbox.Utility.Noise.Parameters( Seed + m, modelEntry.NoiseScale );

			if ( modelEntry.NoiseType == ScattererNoiseType.Perlin )
				_modelNoiseFields[m] = Sandbox.Utility.Noise.PerlinField( parameters );
			else if ( modelEntry.NoiseType == ScattererNoiseType.Simplex )
				_modelNoiseFields[m] = Sandbox.Utility.Noise.SimplexField( parameters );
		}

		RebuildRenderCache();
	}
}
