# md-viewer icon

A recoloring of Minerva's master icon (`D:\dev\proj\minerva\src\Minerva.App\Assets\MinervaIconMaster.png`), which already has the family's square canvas, thin inset border, and centered bold M. The two flat colors were swapped with ImageMagick; the Minerva assets are unchanged.

```powershell
magick MinervaIconMaster.png -fill '#0D2716' -opaque '#32152F' -fill '#93EFA8' -opaque '#D8B4F8' MdViewerIconMaster.png
```

Colors: deep green field `#0D2716`, luminous green letter and border `#93EFA8`.

`MdViewerIconMaster.png` is the master. `scripts/build-icons.ps1` uses ImageMagick only to resize and encode the Windows PNG and multi-resolution ICO assets. Regeneration is not required to build or install the app.
