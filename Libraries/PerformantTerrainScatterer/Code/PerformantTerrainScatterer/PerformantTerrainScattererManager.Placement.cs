using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Sandbox;

public sealed partial class PerformantTerrainScatterer
{
	private List<InstanceData> CalculateChunkPlacements( int cx, int cy, Transform terrainTransform )
	{
		if ( _heightMapPixels == null || _controlMapPixels == null ) return null;
		var modelEntries = ModelEntries;
		if ( modelEntries == null || modelEntries.Length == 0 ) return null;

		var instances = new List<InstanceData>( MaxInstancesPerChunk );
		int chunkSeed = HashCombine( Seed, HashCombine( cx, cy ) );
		var random = new Random( chunkSeed );
		float textureEps = 1.0f / (_resolution - 1);
		float chunkBaseX = cx * ChunkSize;
		float chunkBaseY = cy * ChunkSize;
		int modelsCount = modelEntries.Length;

		for ( int i = 0; i < MaxInstancesPerChunk; i++ )
		{
			float localX = random.NextSingle() * ChunkSize;
			float localY = random.NextSingle() * ChunkSize;
			float worldX = chunkBaseX + localX;
			float worldY = chunkBaseY + localY;

			Vector3 worldAbsPoint = new Vector3( worldX, worldY, 0f );
			Vector3 terrainLocal = terrainTransform.PointToLocal( worldAbsPoint );
			float u = terrainLocal.x / _terrainSize;
			float v = terrainLocal.y / _terrainSize;

			if ( u < 0f || u > 1f || v < 0f || v > 1f ) continue;

			int cX = (int)(u * (_resolution - 1f)).Clamp( 0f, _resolution - 1f );
			int cY = (int)(v * (_resolution - 1f)).Clamp( 0f, _resolution - 1f );
			int controlIndex = cX + cY * _resolution;
			uint packedMap = _controlMapPixels[controlIndex];
			var compactMat = new CompactTerrainMaterial( packedMap );

			if ( compactMat.IsHole ) continue;

			int dominantMaterialId = (compactMat.BlendFactor > 127)
			   ? compactMat.OverlayTextureId
			   : compactMat.BaseTextureId;

			float materialTotalWeight = 0f;
			for ( int m = 0; m < modelsCount; m++ )
			{
				if ( dominantMaterialId < _allowedModelIndices[m].Length && _allowedModelIndices[m][dominantMaterialId] )
				{
					materialTotalWeight += modelEntries[m].Weight;
				}
			}

			if ( materialTotalWeight <= 0f ) continue;

			float roll = random.NextSingle() * materialTotalWeight;
			float currentWeightSum = 0f;
			int selectedModelIndex = -1;

			for ( int m = 0; m < modelsCount; m++ )
			{
				if ( dominantMaterialId < _allowedModelIndices[m].Length && _allowedModelIndices[m][dominantMaterialId] )
				{
					currentWeightSum += modelEntries[m].Weight;
					if ( roll <= currentWeightSum )
					{
						selectedModelIndex = m;
						break;
					}
				}
			}

			if ( selectedModelIndex == -1 ) continue;
			ref var selectedModel = ref modelEntries[selectedModelIndex];

			if ( ReduceDensityOnTransitions )
			{
				float blendNorm = compactMat.BlendFactor * 0.003921569f;
				float purity = MathF.Abs( blendNorm - 0.5f ) * 2f;
				float spawnChance = MathF.Pow( purity, TransitionThinningIntensity );
				if ( random.NextSingle() > spawnChance ) continue;
			}

			if ( selectedModel.NoiseType != ScattererNoiseType.None )
			{
				var noiseField = _modelNoiseFields[selectedModelIndex];
				float noiseVal = noiseField.Sample( worldX, worldY );
				if ( noiseVal < selectedModel.NoiseThreshold ) continue;
			}

			Vector3 normal = Vector3.Up;

			if ( UseTerrainNormal || MaxSlopeAngle < 90f )
			{
				float hL = SampleHeightBilinear( u - textureEps, v );
				float hR = SampleHeightBilinear( u + textureEps, v );
				float hD = SampleHeightBilinear( u, v - textureEps );
				float hU = SampleHeightBilinear( u, v + textureEps );
				Vector3 terrainTangentX = new Vector3( 2.0f * textureEps * _terrainSize, 0f, (hR - hL) * _terrainHeight );
				Vector3 terrainTangentY = new Vector3( 0f, 2.0f * textureEps * _terrainSize, (hU - hD) * _terrainHeight );
				Vector3 terrainNormalLocal = Vector3.Cross( terrainTangentX, terrainTangentY ).Normal;
				normal = terrainTransform.Rotation * terrainNormalLocal;
				float slopeAngle = Vector3.GetAngle( Vector3.Up, normal );
				if ( slopeAngle > MaxSlopeAngle ) continue;
			}

			float z = SampleHeightBilinear( u, v ) * _terrainHeight;
			Vector3 finalLocalPoint = new Vector3( terrainLocal.x, terrainLocal.y, z );
			Vector3 worldTerrainPoint = terrainTransform.PointToWorld( finalLocalPoint );

			Rotation randomYaw = RandomizeRotation
			   ? Rotation.FromYaw( random.NextSingle() * 360f )
			   : Rotation.Identity;

			Rotation finalRot = UseTerrainNormal
			   ? Rotation.FromToRotation( Vector3.Up, normal ) * randomYaw
			   : randomYaw;

			float scale = selectedModel.RandomizeScale
			   ? (random.NextSingle() * (selectedModel.MaxScale - selectedModel.MinScale) + selectedModel.MinScale)
			   : 1.0f;

			instances.Add( new InstanceData
			{
				Position = worldTerrainPoint,
				Rotation = finalRot,
				Scale = scale,
				ModelIndex = selectedModelIndex
			} );
		}

		return instances;
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private int HashCombine( int seed, int value )
	   => seed ^ (value + -1640531527 + (seed << 6) + (seed >> 2));

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private bool IsObstruction( GameObject hitObject )
	{
		if ( hitObject == null ) return false;
		if ( _cachedHitObject == hitObject ) return _cachedIsObstruction;
		_cachedHitObject = hitObject;
		_cachedIsObstruction = hitObject.Components.Get<Terrain>() == null;
		return _cachedIsObstruction;
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	private float SampleHeightBilinear( float u, float v )
	{
		u = u.Clamp( 0f, 1f );
		v = v.Clamp( 0f, 1f );
		float gx = u * (_resolution - 1);
		float gy = v * (_resolution - 1);
		int ix = (int)gx;
		int iy = (int)gy;
		float fx = gx - ix;
		float fy = gy - iy;
		int ix1 = Math.Min( ix + 1, _resolution - 1 );
		int iy1 = Math.Min( iy + 1, _resolution - 1 );
		int row0 = iy * _resolution;
		int row1 = iy1 * _resolution;
		float h00 = _heightMapPixels[row0 + ix] * UshortMaxReciprocal;
		float h10 = _heightMapPixels[row0 + ix1] * UshortMaxReciprocal;
		float h01 = _heightMapPixels[row1 + ix] * UshortMaxReciprocal;
		float h11 = _heightMapPixels[row1 + ix1] * UshortMaxReciprocal;
		float h0 = h00 + (h10 - h00) * fx;
		float h1 = h01 + (h11 - h01) * fx;
		return h0 + (h1 - h0) * fy;
	}
}
