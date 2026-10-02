# Juancho Studio

Juancho-modified AssetStudio build project.

## Current feature

Adds **Juancho → Replace Selected Texture2D** to AssetStudio. Choose the replacement image and texture settings, then save directly back into the currently opened Unity asset or bundle.

The replacement code validates settings, uses the native texture encoder for compressed formats, includes external TypeTree support, writes through a temporary file, atomically replaces the currently opened Unity file, keeps a `.bak` backup of the previous file, and verifies the Texture2D after the in-place commit.

## Build

The Windows x64 GitHub Actions workflow clones upstream AssetStudio, applies the Juancho patch, builds the native texture decoder and texture encoder, fetches `classdata.tpk`, publishes a self-contained .NET 8 Windows build, and packages it as a ZIP.
