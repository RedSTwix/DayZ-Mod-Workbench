from pathlib import Path
import struct, shutil, math, re, hashlib, json, os, tempfile, subprocess

PIXEL_SHADER_NAMES = [
    'Normal','NormalDXTA','NormalMap','NormalMapThrough','NormalMapGrass','NormalMapDiffuse',
    'Detail','Interpolation','Water','WaterSimple','White','WhiteAlpha','AlphaShadow','AlphaNoShadow',
    'Dummy0','DetailMacroAS','NormalMapMacroAS','NormalMapDiffuseMacroAS','NormalMapSpecularMap',
    'NormalMapDetailSpecularMap','NormalMapMacroASSpecularMap','NormalMapDetailMacroASSpecularMap',
    'NormalMapSpecularDIMap','NormalMapDetailSpecularDIMap','NormalMapMacroASSpecularDIMap',
    'NormalMapDetailMacroASSpecularDIMap','Terrain1','Terrain2','Terrain3','Terrain4','Terrain5','Terrain6',
    'Terrain7','Terrain8','Terrain9','Terrain10','Terrain11','Terrain12','Terrain13','Terrain14','Terrain15',
    'TerrainSimple1','TerrainSimple2','TerrainSimple3','TerrainSimple4','TerrainSimple5','TerrainSimple6',
    'TerrainSimple7','TerrainSimple8','TerrainSimple9','TerrainSimple10','TerrainSimple11','TerrainSimple12',
    'TerrainSimple13','TerrainSimple14','TerrainSimple15','Glass','NonTL','NormalMapSpecularThrough','Grass',
    'NormalMapThroughSimple','NormalMapSpecularThroughSimple','Road','Shore','ShoreWet','Road2Pass','ShoreFoam',
    'NonTLFlare','NormalMapThroughLowEnd','TerrainGrass1','TerrainGrass2','TerrainGrass3','TerrainGrass4',
    'TerrainGrass5','TerrainGrass6','TerrainGrass7','TerrainGrass8','TerrainGrass9','TerrainGrass10',
    'TerrainGrass11','TerrainGrass12','TerrainGrass13','TerrainGrass14','TerrainGrass15','Crater1','Crater2',
    'Crater3','Crater4','Crater5','Crater6','Crater7','Crater8','Crater9','Crater10','Crater11','Crater12',
    'Crater13','Crater14','Sprite','SpriteSimple','Cloud','Horizon','Super','Multi','TerrainX','TerrainSimpleX',
    'TerrainGrassX','Tree','TreePRT','TreeSimple','Skin','CalmWater','TreeAToC','GrassAToC','TreeAdv',
    'TreeAdvSimple','TreeAdvTrunk','TreeAdvTrunkSimple','TreeAdvAToC','TreeAdvSimpleAToC','TreeSN','SpriteExtTi',
    'TerrainSNX','SimulWeatherClouds','SimulWeatherCloudsWithLightning','SimulWeatherCloudsCPU',
    'SimulWeatherCloudsWithLightningCPU','SuperExt','SuperAToC','None'
]

VERTEX_SHADER_NAMES = [
    'Basic','NormalMap','NormalMapDiffuse','Grass','Dummy1','Dummy2','ShadowVolume','Water','WaterSimple',
    'Sprite','Point','NormalMapThrough','Dummy3','Terrain','BasicAS','NormalMapAS','NormalMapDiffuseAS','Glass',
    'NormalMapSpecularThrough','NormalMapThroughNoFade','NormalMapSpecularThroughNoFade','Shore','TerrainGrass',
    'Super','Multi','Tree','TreeNoFade','TreePRT','TreePRTNoFade','Skin','CalmWater','TreeAdv','TreeAdvTrunk',
    'SimulWeatherClouds','SimulWeatherCloudsCPU'
]

UV_SOURCE_NAMES = {
    0:'none', 1:'tex', 2:'texWaterAnim', 3:'pos', 4:'norm', 5:'tex1',
    6:'worldPos', 7:'worldNorm', 8:'texShoreAnim', 9:'none'
}

TEXTURE_FILTER_NAMES = {0:'Point',1:'Linear',2:'Trilinear',3:'Anisotropic'}
