#!/usr/bin/env python3
"""Generate app icon with letter W - similar to the K icon style."""

from PIL import Image, ImageDraw, ImageFont
import os

# Icon size
SIZE = 256
CENTER = SIZE // 2

# Create image with transparent background
img = Image.new('RGBA', (SIZE, SIZE), (0, 0, 0, 0))
draw = ImageDraw.Draw(img)

# Dark gradient background (rounded square)
bg_color = (30, 30, 40, 255)  # Dark gray/blue-black
corner_radius = 50

# Draw rounded rectangle background
draw.rounded_rectangle(
    [(0, 0), (SIZE-1, SIZE-1)],
    radius=corner_radius,
    fill=bg_color
)

# Try to use a bold font, fallback to default if not available
try:
    # Try system fonts
    font_paths = [
        "C:/Windows/Fonts/segoeuib.ttf",  # Segoe UI Bold
        "C:/Windows/Fonts/arialbd.ttf",   # Arial Bold
        "C:/Windows/Fonts/calibrib.ttf",  # Calibri Bold
    ]
    font = None
    for fp in font_paths:
        if os.path.exists(fp):
            font = ImageFont.truetype(fp, 140)
            break
    if font is None:
        font = ImageFont.load_default()
except:
    font = ImageFont.load_default()

# Draw white "W" letter
text = "W"
text_color = (255, 255, 255, 255)  # White

# Get text bounding box for centering
bbox = draw.textbbox((0, 0), text, font=font)
text_width = bbox[2] - bbox[0]
text_height = bbox[3] - bbox[1]

# Center the text
x = (SIZE - text_width) // 2
y = (SIZE - text_height) // 2 - 15  # Slight upward adjustment

# Draw the W
draw.text((x, y), text, font=font, fill=text_color)

# Save as ICO (multiple sizes for Windows)
output_dir = os.path.dirname(os.path.abspath(__file__))
ico_path = os.path.join(output_dir, "app_icon.ico")
png_path = os.path.join(output_dir, "app_icon.png")

# Save ICO with multiple sizes
sizes = [256, 128, 64, 48, 32, 16]
img.save(ico_path, format='ICO', sizes=[(s, s) for s in sizes])
img.save(png_path, format='PNG')

print(f"Icon saved to:")
print(f"  ICO: {ico_path}")
print(f"  PNG: {png_path}")
