"""Regenerate the original usage-meter app icon (Pillow, developer tool only)."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1] / "src" / "CodexTokenOverlay" / "Assets"
root.mkdir(parents=True, exist_ok=True)
s = 4
image = Image.new("RGBA", (256*s, 256*s))
mask = Image.new("L", image.size)
ImageDraw.Draw(mask).rounded_rectangle((8*s, 8*s, 248*s, 248*s), radius=48*s, fill=255)
gradient = Image.new("RGBA", image.size)
g = ImageDraw.Draw(gradient)
for y in range(256*s):
    t = y / (256*s)
    g.line((0, y, 256*s, y), fill=(int(64-43*t), int(72-49*t), int(91-61*t), 255))
image.paste(gradient, (0, 0), mask)
d = ImageDraw.Draw(image)
def rect(box, radius, color):
    d.rounded_rectangle(tuple(int(v*s) for v in box), radius=int(radius*s), fill=color)
rect((57, 148, 81, 204), 12, "#D4C7FF")
rect((111, 115, 135, 204), 12, "#AA90F4")
rect((165, 81, 189, 204), 12, "#8065DE")
d.line(tuple((int(x*s), int(y*s)) for x,y in [(60,173),(117,140),(177,73)]), fill="#BBB0E8", width=7*s)
for x,y,r,color in [(61,174,12,"#E6DFFF"),(119,140,12,"#CDC0FF"),(177,74,18,"#B397FF")]:
    d.ellipse(((x-r)*s,(y-r)*s,(x+r)*s,(y+r)*s), fill=color)
icon = image.resize((256,256), Image.Resampling.LANCZOS)
icon.save(root / "app.png")
icon.save(root / "app.ico", sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
print("Created app.png and multi-resolution app.ico")
