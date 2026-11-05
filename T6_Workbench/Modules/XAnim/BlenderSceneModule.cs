using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    internal sealed class BlenderSceneModule
    {
        const string SceneScript = "import bpy, json, math, os, struct, sys\r\nargs = sys.argv[sys.argv.index('--') + 1:]\r\nsource, output, ik_script = args[0], args[1], args[2]\r\nbpy.ops.object.select_all(action='SELECT')\r\nbpy.ops.object.delete(use_global=False)\r\n# GLB animation times are seconds. Blender converts them to frames using\r\n# the scene FPS during import, so set the native rate before importing.\r\nwith open(source, 'rb') as stream:\r\n    header = stream.read(20)\r\n    if len(header) != 20 or header[:4] != b'glTF' or header[16:20] != b'JSON':\r\n        raise RuntimeError('Invalid GLB header; cannot read XAnim frame rate.')\r\n    json_length = struct.unpack_from('<I', header, 12)[0]\r\n    document = json.loads(stream.read(json_length))\r\nrates = {node.get('extras', {}).get('t6XAnimFrameRate') for node in document.get('nodes', [])\r\n         if isinstance(node.get('extras'), dict) and 't6XAnimFrameRate' in node['extras']}\r\nif len(rates) != 1:\r\n    raise RuntimeError('XAnim frame rate metadata is missing or ambiguous; GLB was retained.')\r\nframe_rate = rates.pop()\r\nif not isinstance(frame_rate, int) or not 1 <= frame_rate <= 32767:\r\n    raise RuntimeError('Unsupported XAnim frame rate: %r' % frame_rate)\r\nbpy.context.scene.render.fps = frame_rate\r\nbpy.context.scene.render.fps_base = 1.0\r\nbpy.ops.import_scene.gltf(filepath=source)\r\narmatures = [obj for obj in bpy.context.scene.objects if obj.type == 'ARMATURE' and obj.data.bones.get('tag_camera')]\r\nif not armatures:\r\n    raise RuntimeError('The exported model has no tag_camera bone; the GLB was retained.')\r\nanimated = [obj for obj in armatures if obj.animation_data and obj.animation_data.action]\r\nif len(animated) == 1:\r\n    armature = animated[0]\r\nelif len(armatures) == 1:\r\n    armature = armatures[0]\r\nelse:\r\n    raise RuntimeError('More than one armature has tag_camera; cannot select a camera parent safely.')\r\ncamera_data = bpy.data.cameras.new('Workbench Camera')\r\ncamera_data.clip_start = 0.01\r\ncamera_data.clip_end = 1000.0\r\ncamera = bpy.data.objects.new('Workbench Camera', camera_data)\r\nbpy.context.scene.collection.objects.link(camera)\r\nbpy.ops.object.select_all(action='DESELECT')\r\ncamera.select_set(True)\r\narmature.select_set(True)\r\nbpy.context.view_layer.objects.active = armature\r\narmature.data.bones.active = armature.data.bones['tag_camera']\r\nbpy.ops.object.parent_set(type='BONE_RELATIVE', keep_transform=False)\r\ncamera.location = (0, 0, 0)\r\ncamera.rotation_mode = 'XYZ'\r\ncamera.rotation_euler = (math.radians(90), 0, math.radians(-90))\r\nscene = bpy.context.scene\r\nscene.camera = camera\r\nscene.unit_settings.system = 'METRIC'\r\nscene.unit_settings.scale_length = 1.0\r\nfor obj in scene.objects:\r\n    if obj.type == 'ARMATURE':\r\n        for bone in obj.pose.bones:\r\n            if bone.custom_shape and bone.custom_shape.name.startswith('Icosphere'):\r\n                bone.custom_shape_scale_xyz = (0.2, 0.2, 0.2)\r\nif armature.animation_data and armature.animation_data.action:\r\n    action_range = armature.animation_data.action.frame_range\r\n    scene.frame_start = int(math.floor(action_range[0]))\r\n    scene.frame_end = max(scene.frame_start + 1, int(math.ceil(action_range[1])))\r\n    scene.frame_set(scene.frame_start)\r\nwith open(ik_script, 'r', encoding='utf-8-sig') as stream:\r\n    ik_code = stream.read()\r\nik_namespace = {'__name__': 'workbench_ik'}\r\nexec(compile(ik_code, ik_script, 'exec'), ik_namespace)\r\nik_namespace['setup'](armature)\r\ntext = bpy.data.texts.new('Workbench IK.py')\r\ntext.write(ik_code)\r\ntext.use_module = True\r\nhelp_text = bpy.data.texts.new('Workbench IK - README')\r\nhelp_text.write('Open the 3D View sidebar (N), Workbench IK. If the panel is missing after reopening, open Workbench IK.py in the Text Editor and click Run Script. No global Auto Run setting is required. Select a WB_IK wrist or finger control, click Enable selected control, move it with G (R rotates wrists), and insert keys with I. IK starts disabled to preserve the imported clip. Target keys are sampled on every source frame; edit or remove the desired target keys in the Dope Sheet. Set the scene frame range, then click Bake IK to game bones. The original action is retained as FK_Backup. For animation-only export, select the hands armature and its skinned meshes; enable Selected Objects, Scene animation mode, sampling, frame range and Custom Properties. Exclude helper controls, the reference cube and weapon armatures. Do not export all actions, including FK_Backup. Save the blend after editing.')\r\nbpy.ops.mesh.primitive_cube_add(size=2, location=(35, 0, 0))\r\nbpy.context.object.name = 'World reference cube 2m'\r\nbpy.ops.file.pack_all()\r\nbpy.ops.wm.save_as_mainfile(filepath=output)\r\nprint('WORKBENCH_BLEND_SAVED ' + output)";

        internal static string FindBlender(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = Path.GetFullPath(configured.Trim());
                if (!File.Exists(path)) 
                    throw new FileNotFoundException(EnglishText.Text("Blender not found. Specify blender.exe in Settings."), path);
                return path;
            }
            string environmentPath = Environment.GetEnvironmentVariable("BLENDER");
            if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath)) return Path.GetFullPath(environmentPath);
            string programDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            if (Directory.Exists(programDirectory))
            {
                string installed = Directory.EnumerateDirectories(programDirectory, "Blender*") 
                    .OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Replace("Blender ", ""), out Version version) ? version : new Version(0, 0))
                    .Select(path => Path.Combine(path, "blender.exe")).FirstOrDefault(File.Exists);
                if (installed != null) return installed;
            }
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), "blender.exe");
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
            }
            throw new FileNotFoundException(EnglishText.Text("Blender not found. Specify blender.exe in Workbench Settings."));
        }

        internal async Task<string> CreateAsync(string blender, string glb, CancellationToken cancellation)
        {
            if (!File.Exists(glb)) throw new FileNotFoundException(EnglishText.Text("Exported GLB not found."), glb);
            string basePath = Path.ChangeExtension(Path.GetFullPath(glb), null);
            string output = basePath + ".blend";
            for (int index = 1; File.Exists(output); index++)
            {
                output = basePath + "." + index.ToString("000") + ".blend";
            }
            string ikScript = Path.Combine(AppContext.BaseDirectory, "resources", "xanim", "workbench_ik.py");
            if (!File.Exists(ikScript)) throw new FileNotFoundException("Workbench IK script was not found.", ikScript);
            var result = await new ToolRunner().RunAsync(blender,
                Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--background" }, new string[] { "--factory-startup" })), new string[] { "--python-exit-code" })), new string[] { "1" })), new string[] { "--python-expr" })), new string[] { SceneScript })), new string[] { "--" })), new string[] { Path.GetFullPath(glb) })), new string[] { output })), new string[] { ikScript })),
                Path.GetDirectoryName(Path.GetFullPath(glb)), null, cancellation);
            if (result.ExitCode != 0 || !File.Exists(output))
                throw new InvalidDataException(EnglishText.Text("Blender did not create .blend. GLB saved: ") + glb + "\n" + result.Output);
            return output;
        }
    }
}
