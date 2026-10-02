# Juancho Studio

Juancho-modified AssetStudio build project.

## Current feature

Adds **Juancho → Replace Selected Texture2D** to AssetStudio. The flow lets you choose an image, confirm Texture2D dimensions/format/filter/wrap/mipmap/channel settings, then save a new Unity asset or bundle.

The replacement code validates settings, uses the native texture encoder for compressed formats, includes external TypeTree support, writes atomically through a temporary file, and verifies the saved Texture2D before committing the final output.

## Build

The Windows x64 GitHub Actions workflow clones upstream AssetStudio, applies the Juancho patch, builds the native texture decoder and texture encoder, fetches `classdata.tpk`, publishes a self-contained .NET 8 Windows build, and packages it as a ZIP.
