import os
from PIL import Image

def process_images(input_folder):
    for filename in os.listdir(input_folder):
        if filename.endswith('_Alb.png'):  # Assuming .png, you can add other formats if needed
            img_path = os.path.join(input_folder, filename)
            img = Image.open(img_path).convert("RGBA")

            # Create a new image with the same size and black background
            new_img = Image.new("RGBA", img.size, (0, 0, 0, 255))

            datas = img.getdata()
            new_data = []
            for item in datas:
                # Change non-transparent pixels to white and keep black for transparent pixels
                if item[3] > 0:  # Pixel is not transparent
                    new_data.append((255, 255, 255, 255))
                else:
                    new_data.append((0, 0, 0, 255))

            new_img.putdata(new_data)

            # Change filename from _Alb to _Opa
            new_filename = filename.replace('_Alb', '_Opa')
            new_path = os.path.join(input_folder, new_filename)

            # Save the processed image
            new_img.save(new_path)

    print(f"Processed images saved to {input_folder}")

# Specify your input folder here
input_folder = 'Textures'

process_images(input_folder)
