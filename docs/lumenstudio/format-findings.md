# LumenStudio format findings (Green, observed corpus)

Measured over all 229 `lumendata/packed/**/packeddata.ddp` archives of the original
Japanese data (19,850 textures) with `tools/Probe`. These are the facts the writers rely on.

## DDP (`LM_NUT_TYPE1`)

- Trailer (16 bytes before the movie block): `movieBlockLength`, `textureBlockLength`,
  `maxTextureEntryLength`, `textureCount`.
- The third word was previously "unknown". In **229/229** archives (original, translated
  and the vertical-scroll mods) it equals the byte length of the largest texture entry.
  It is almost certainly the size of the game's per-texture load buffer, so any writer that
  grows a texture **must** update it, or the original PS3/System 357 executable can overflow.
- The fourth word equals the texture count except in one archive (`enso_mh3G`: 146 vs 144);
  it is preserved as-is.
- Texture entries are stored contiguously (no gaps), every relative offset is a multiple of 16.
- Movie relative offsets are only 4-byte aligned; the texture block starts directly after the
  movie block (no alignment).
- `packlist.txt` next to each archive lists movie names and their NUT names, in directory order.

## NTP3 / NUT

- All textures: version 1, exactly one texture per NUT, one mip level, header size `0x50`,
  no non-zero `GIDX` id. Formats: BC3 (16,068), ARGB 14 (3,704), ARGB 17 (78). No BC1 in Green.
- Header (big-endian, relative to the texture header at file offset 0x10):
  `+0x00 totalSize`, `+0x08 dataSize`, `+0x0C headerSize (0x50)`, `+0x11 mipCount`,
  `+0x13 format`, `+0x14 width`, `+0x16 height`, `+0x30 "eXt\0" 0x20 0x10 0`, `+0x40 "GIDX" 0x10 0 0`.
- `dataSize = align16(rawPayload)`; `totalSize = 0x50 + dataSize`; DDP entry = `0x10 + totalSize`.
- Pixel payloads are linear (not swizzled). 16,720 textures are non-power-of-two and 1,415 BC3
  textures have dimensions that are not multiples of 4, so NPOT sizes are valid on hardware.
  Largest dimension observed: 1713.

## LMB

- Shape vertex UVs are normalised (0..1). On-screen size comes from vertex X/Y, so replacing a
  texture with a higher (or lower) resolution image keeps its displayed size.
- `F007` holds one entry per texture: `textureIndex, nameIndex(0), width:float, height:float`.
  It is the only record with texture dimensions and is updated on resize.
- `F00C` words 8/9 are the stage size (1280×720), word 7 the frame rate.

## NUT_PACK_TYPE1 (`nutdatapack.ndp`)

- Same preamble as a DDP; entry count at `0x3E`, then 9 bytes, then entries
  `nameLength, name, [5 bytes after the first entry], relativeOffset, length`, then one word with the
  data-block length, then the NUTs (contiguous). 348/348 archives round-trip byte-identically.
- There is no largest-entry word, so resized NDP textures are reported as a warning to test on hardware.
- 11,306 entries are single v1 textures, 89 hold several textures; LumenStudio indexes them flat.

## Standalone NUT files (`data/nutdata`, `data/don3d`, …)

- 1,794 files: v1 single (168), v1 multi (775), v2 multi (726), v2 single (123). v2 stores the header
  table first and payloads after it, each header's `+0x20` being the payload offset relative to that
  header. Extracting any texture and splicing it back is byte-identical for all of them.
- The two font atlases (`data/font/*.nut`) use format 1 = BC2/DXT3 (explicit 4-bit alpha); supported.
- Clone families span layers: e.g. battle backgrounds in `nutdata/S10100-1/battle/bg` are identical to
  textures in `battle/chat`, `battle/enso_stage` and `battle/intro` DDPs.
