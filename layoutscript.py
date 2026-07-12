import yaml
import math
import random

def construct_l(loader, node):
    return loader.construct_scalar(node)

def construct_ul(loader, node):
    return loader.construct_scalar(node)

def construct_u(loader, node):
    return loader.construct_scalar(node)

yaml.SafeLoader.add_constructor('!l', construct_l)
yaml.SafeLoader.add_constructor('!ul', construct_ul)
yaml.SafeLoader.add_constructor('!u', construct_u)

def grad_to_rad(degrees):
    return [round(math.radians(float(deg)), 5) for deg in degrees]

def scale_needed(scale):
    return any(float(scale[axis]) not in {0.0, 1.0} for axis in ['X', 'Y', 'Z'])

def generate_hash(length):
    return ''.join(random.choice('0123456789') for _ in range(length))

def generate_instance_id():
    return '{}-{}-{}-{}-{}'.format(
        generate_hash(8),
        generate_hash(4),
        generate_hash(4),
        generate_hash(4),
        generate_hash(12)
    )

def convert_team(team):
    team_mapping = {
        '!l 0': 'Neutral',
        '!l 1': 'Alpha',
        '!l 2': 'Bravo'
    }
    return {'Team': team_mapping.get(team, 'Neutral')}

def convert_translate(translate):
    return [format(float(translate[axis]) / 10, '.5f') for axis in ['X', 'Y', 'Z']]

def convert_object(obj):
    new_obj = {}
    if 'Rotate' in obj:
        new_obj['Rotate'] = grad_to_rad([obj['Rotate']['X'], obj['Rotate']['Y'], obj['Rotate']['Z']])
    if 'Translate' in obj:
        new_obj['Translate'] = convert_translate(obj['Translate'])
    unit_config_name = obj.get('UnitConfigName', '')
    if unit_config_name == 'RespawnPos':
        unit_config_name = 'Obj_RespawnPos'
    final_name = unit_config_name.replace("Enm_", "Enemy")
    new_obj['Name'] = final_name
    new_obj['Gyaml'] = final_name
    hash_length = 19
    new_obj['Hash'] = '!ul ' + generate_hash(hash_length)
    new_obj['InstanceID'] = generate_instance_id()
    new_obj['TeamCmp'] = convert_team(obj.get('Team', '!l 0'))
    new_obj['Layer'] = obj.get('LayerConfigName', 'DefaultLayer')
    if 'Scale' in obj and scale_needed(obj['Scale']):
        new_obj['Scale'] = obj['Scale']
    new_obj['Phive'] = {'Placement': {'ID': new_obj['Hash']}}
    new_obj['SRTHash'] = '!u ' + generate_hash(8)
    return new_obj

def convert_yaml(input_filename, output_filename):
    with open(input_filename, 'r', encoding='utf-8') as f:
        data = yaml.safe_load(f)
    
    if not isinstance(data, dict) or 'root' not in data or 'Objs' not in data['root']:
        raise TypeError("Expected 'root' key with a list of objects in the YAML file")

    objs = data['root']['Objs']

    converted_objs = []
    for obj in objs:
        if not isinstance(obj, dict):
            raise TypeError("Expected a dictionary for each object in the 'Objs' list")
        converted_objs.append(convert_object(obj))
    
    output_data = {
        'Version': 7,
        'IsBigEndian': False,
        'SupportPaths': False,
        'HasReferenceNodes': False,
        'root': {
            'Actors': {
                'Objs': converted_objs
            }
        }
    }

    with open(output_filename, 'w', encoding='utf-8') as f:
        yaml.dump(output_data, f, default_flow_style=False, indent=2)

if __name__ == "__main__":
    input_filename = 'OldLayout.yaml'
    output_filename = 'Sp3Layout.yaml'

    convert_yaml(input_filename, output_filename)
