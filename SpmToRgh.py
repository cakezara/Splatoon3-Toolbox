import os
from PIL import Image, ImageOps

def invert_image_colors(input_folder, output_folder=None):
    if output_folder is None:
        output_folder = input_folder

    if not os.path.exists(output_folder):
        os.makedirs(output_folder)

    for filename in os.listdir(input_folder):
        if filename.lower().endswith(('.png', '.jpg', '.jpeg', '.bmp', '.gif', '.tiff')):
            img_path = os.path.join(input_folder, filename)
            img = Image.open(img_path)

            # Invert colors
            inverted_image = ImageOps.invert(img.convert('RGB'))

            # Save the inverted image
            inverted_image.save(os.path.join(output_folder, filename))

    print(f"Inverted images saved to {output_folder}")

# Specify your input folder here
input_folder = 'Textures'
# Specify your output folder here (or None to overwrite the input folder)
output_folder = 'New_Textures'  # Set to None to overwrite original images

invert_image_colors(input_folder, output_folder)
