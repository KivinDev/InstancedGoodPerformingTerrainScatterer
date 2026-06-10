# Performant Terrain Scatterer

Welcome to the **Performant Terrain Scatterer**. This is a drop-in S&box component designed to automatically populate your terrain with trees, grass, rocks, or any other clutter you need, without tanking your framerate. 

Instead of hand-placing thousands of props, you give this tool a list of models, tell it how you want them to behave, and it handles the heavy lifting in the background. It dynamically loads and unloads sections of the world as the camera moves, keeping things incredibly optimized.

## How to Use It

1. Add the **PerformantTerrainScatterer** component to an object in your scene.
2. Assign your **TargetTerrain** in the inspector.
3. Add a few models to the **Models** list.
4. Tweak your settings for density, scaling, and noise.
5. Click the **Reinitialize** button in the editor to see your changes instantly.

## Why It's Fast

### Background Processing
Chunk calculation happens on separate threads. As you walk around, the scatterer predicts what needs to load next and builds it in the background to prevent frame hitching.

### Smart Rendering
It uses instanced rendering to draw thousands of identical models in a single draw call. It also natively supports distance-based Level of Detail (LOD) swapping and frustum culling, meaning it outright refuses to spend resources drawing things outside of your camera's view.

### Chunk Management
The system splits your world into a grid. As the camera moves, it seamlessly brings neighboring chunks into existence and deletes the ones you left behind to free up memory.

## Natural Placement Features

### Material Filtering
You can tell the scatterer to only place certain models on specific terrain materials. For example, pine trees only spawn on dirt, and grass only spawns on the grass texture. It reads your terrain's control map to figure out exactly where the borders are. 

### Obstruction Avoidance
Nobody likes trees clipping through buildings. The component casts rays downward to check for solid objects. If it hits a house, a trigger, or another prop, it cancels the spawn so your map stays clean. 

### Slopes and Normals
You can set a maximum slope angle to stop vegetation from growing sideways on sheer cliffs. You can also tell models to align themselves to the curve of the terrain or stand perfectly straight.

### Organic Grouping
Instead of scattering everything in a perfectly random, chaotic mess, you can enable Perlin or Simplex noise. This forces models to grow in natural-looking clusters and patches, mimicking how flora actually grows in the wild.
