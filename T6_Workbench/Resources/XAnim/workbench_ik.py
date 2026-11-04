"""Workbench hand controls. Run this text to restore the panel after reopening a blend."""
import bpy
from mathutils import Matrix

PREFIX = 'WB_IK_'

def controls(rig):
    return [o for o in bpy.data.objects if o.get('wb_ik_rig') == rig.name]

def rig_for(context):
    obj = context.object
    if obj and obj.get('wb_ik_rig'):
        return bpy.data.objects.get(obj['wb_ik_rig'])
    if obj and obj.type == 'ARMATURE' and obj.get('wb_ik_ready'):
        return obj
    return None

def snap(control, rig):
    bone = rig.pose.bones[control['wb_ik_bone']]
    matrix = rig.matrix_world @ bone.matrix
    control.matrix_world = matrix

def setup(rig):
    if rig.get('wb_ik_ready'):
        return
    scene, frame = bpy.context.scene, bpy.context.scene.frame_current
    scene.frame_set(scene.frame_start)
    specs = []
    for side in ('le', 'ri'):
        arm = ['j_shoulder_'+side, 'j_elbow_'+side, 'j_wrist_'+side]
        if not all(n in rig.pose.bones for n in arm):
            continue
        specs.append((side+'_wrist', arm, None))
        for finger in ('thumb', 'index', 'mid', 'ring', 'pinky'):
            names = ['j_%s_%s_%d' % (finger, side, i) for i in range(4)]
            if all(n in rig.pose.bones for n in names):
                specs.append((side+'_'+finger, names, 'j_wrist_'+side))
    if not specs:
        return
    from mathutils import Vector
    def aligned(a, b, reference):
        y = (b-a).normalized()
        z = reference.to_3x3().col[2].normalized()
        x = y.cross(z)
        if x.length < 1e-6:
            x = y.cross(reference.to_3x3().col[0])
        x.normalize(); z = x.cross(y).normalized()
        m = Matrix((x,y,z)).transposed().to_4x4();m.translation=a
        return m
    collection = bpy.data.collections.new('Workbench IK controls')
    scene.collection.children.link(collection)
    data=bpy.data.armatures.new('Workbench IK solver')
    solver=bpy.data.objects.new('Workbench IK solver',data)
    collection.objects.link(solver)
    solver.parent=rig
    solver.hide_render=True
    solver.hide_set(True)
    rig['wb_ik_solver']=solver.name
    bpy.ops.object.select_all(action='DESELECT')
    solver.hide_set(False);solver.select_set(True)
    bpy.context.view_layer.objects.active=solver
    bpy.ops.object.mode_set(mode='EDIT')
    for label,names,parent in specs:
        previous=data.edit_bones.get('OUT_'+parent) if parent else None
        for i,name in enumerate(names[:-1]):
            bone=data.edit_bones.new('SOLVE_'+name)
            bone.head=rig.pose.bones[name].head
            bone.tail=rig.pose.bones[names[i+1]].head
            bone.parent=previous
            bone.use_connect=False
            previous=bone
        for i,name in enumerate(names):
            bone=data.edit_bones.new('OUT_'+name)
            bone.matrix=rig.pose.bones[name].matrix
            bone.length=max(rig.pose.bones[name].length,0.01)
            bone.parent=data.edit_bones['SOLVE_'+names[min(i,len(names)-2)]]
    bpy.ops.object.mode_set(mode='OBJECT')
    # Sample FK into an independent solver. Its segments end at actual joint
    # heads, never at the arbitrary display tails created by the GLB importer.
    for f in range(scene.frame_start,scene.frame_end+1):
        scene.frame_set(f)
        for label,names,parent in specs:
            for i,name in enumerate(names[:-1]):
                bone=solver.pose.bones['SOLVE_'+name]
                a,b=rig.pose.bones[name].head,rig.pose.bones[names[i+1]].head
                bone.matrix=aligned(a,b,rig.pose.bones[name].matrix)
                bpy.context.view_layer.update()
                for prop in ('location','rotation_quaternion','scale'):
                    bone.rotation_mode='QUATERNION';bone.keyframe_insert(prop,frame=f)
            for name in names:
                bone=solver.pose.bones['OUT_'+name]
                bone.rotation_mode='QUATERNION';bone.matrix=rig.pose.bones[name].matrix
                bpy.context.view_layer.update()
                for prop in ('location','rotation_quaternion','scale'):bone.keyframe_insert(prop,frame=f)
    for label,names,parent in specs:
        obj=bpy.data.objects.new(PREFIX+label,None);collection.objects.link(obj)
        obj.empty_display_type='CUBE' if parent is None else 'SPHERE'
        obj.empty_display_size=max(rig.pose.bones[names[-1]].length*0.3,0.01)
        obj.show_in_front=True;obj.hide_render=True;obj.rotation_mode='QUATERNION'
        obj['wb_ik_rig']=rig.name;obj['wb_ik_bone']=names[-1];obj['wb_ik_owner']=names[-2]
        obj['wb_ik_kind']='wrist' if parent is None else 'finger'
        obj.parent=solver
        if parent:
            obj.parent_type='BONE';obj.parent_bone='OUT_'+parent
        for f in range(scene.frame_start,scene.frame_end+1):
            scene.frame_set(f);snap(obj,rig);bpy.context.view_layer.update()
            for prop in ('location','rotation_quaternion','scale'):obj.keyframe_insert(prop,frame=f)
        constraint=solver.pose.bones['SOLVE_'+names[-2]].constraints.new('IK')
        constraint.name=obj.name;constraint.target=obj;constraint.chain_count=len(names)-1
        constraint.use_stretch=False;constraint.influence=0.0;constraint.iterations=500
        if parent is None:
            c=solver.pose.bones['OUT_'+names[-1]].constraints.new('COPY_ROTATION')
            c.name=obj.name;c.target=obj;c.target_space=c.owner_space='WORLD';c.influence=0.0
        for name in names:
            c=rig.pose.bones[name].constraints.new('COPY_TRANSFORMS')
            c.name=obj.name;c.target=solver;c.subtarget='OUT_'+name
            c.target_space=c.owner_space='WORLD';c.influence=0.0
    solver.hide_set(True)
    scene.frame_set(frame);rig['wb_ik_ready']=True

class WB_OT_enable_ik(bpy.types.Operator):
    bl_idname = 'workbench.enable_hand_ik'
    bl_label = 'Enable selected control'
    bl_options = {'REGISTER', 'UNDO'}
    def execute(self, context):
        rig, obj = rig_for(context), context.object
        if not rig or not obj.get('wb_ik_owner'):
            self.report({'ERROR'}, 'Select a WB_IK wrist or finger control first.')
            return {'CANCELLED'}
        # Do not resnap an already enabled chain and discard an edit.
        solver = bpy.data.objects[rig['wb_ik_solver']]
        owned = [c for arm in (rig, solver) for b in arm.pose.bones for c in b.constraints if c.name == obj.name]
        if not any(c.influence for c in owned):
            snap(obj, rig)
            for prop in ('location', 'rotation_quaternion', 'scale'):
                obj.keyframe_insert(prop, frame=context.scene.frame_current)
        for c in owned:
            c.mute = False
            c.influence = 1.0
        context.view_layer.update()
        return {'FINISHED'}

class WB_OT_bake_ik(bpy.types.Operator):
    bl_idname = 'workbench.bake_hand_ik'
    bl_label = 'Bake IK to game bones'
    bl_options = {'REGISTER', 'UNDO'}
    def execute(self, context):
        rig = rig_for(context)
        if not rig:
            self.report({'ERROR'}, 'Select the hands rig or one of its controls.')
            return {'CANCELLED'}
        if any(not c.name.startswith(PREFIX) and not c.mute and c.influence for b in rig.pose.bones for c in b.constraints):
            self.report({'ERROR'}, 'Other active bone constraints exist. Bake those separately first.')
            return {'CANCELLED'}
        if rig.animation_data and any(t for t in rig.animation_data.nla_tracks if not t.mute):
            self.report({'ERROR'}, 'Mute NLA tracks before baking the active action.')
            return {'CANCELLED'}
        scene, frame = context.scene, context.scene.frame_current
        if scene.frame_end - scene.frame_start >= 4096:
            self.report({'ERROR'}, 'The XAnim range must not exceed 4096 frames.')
            return {'CANCELLED'}
        if context.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        bpy.ops.object.select_all(action='DESELECT')
        rig.select_set(True)
        context.view_layer.objects.active = rig
        original = rig.animation_data.action if rig.animation_data else None
        name = original.name if original else 'Workbench_XAnim'
        if original:
            original.use_fake_user = True
        bpy.ops.nla.bake(frame_start=scene.frame_start, frame_end=scene.frame_end,
                         step=1, only_selected=False, visual_keying=True,
                         clear_constraints=False, clear_parents=False,
                         use_current_action=False, clean_curves=False, bake_types={'POSE'})
        if original:
            original.name = name + '_FK_Backup'
        rig.animation_data.action.name = name
        for bone in rig.pose.bones:
            for c in bone.constraints:
                if c.name.startswith(PREFIX):
                    c.influence = 0.0
                    c.mute = True
        scene.frame_set(frame)
        self.report({'INFO'}, 'Baked scene frame range to a new action. IK disabled; original action retained as FK_Backup.')
        return {'FINISHED'}

class WB_PT_hand_ik(bpy.types.Panel):
    bl_label = 'Workbench Hand IK'
    bl_idname = 'WB_PT_hand_ik'
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'Workbench IK'
    def draw(self, context):
        col = self.layout.column()
        col.label(text='Select a WB_IK control; enable it before editing.')
        col.operator('workbench.enable_hand_ik')
        col.label(text='G: move; R: wrist rotation; I: keyframe.')
        col.label(text='Finger controls follow their wrist.')
        col.prop(context.scene, 'frame_start')
        col.prop(context.scene, 'frame_end')
        col.operator('workbench.bake_hand_ik')
        col.label(text='Export the baked active action, not FK_Backup.')

def register():
    for cls in (WB_OT_enable_ik, WB_OT_bake_ik, WB_PT_hand_ik):
        old = getattr(bpy.types, cls.__name__, None)
        if old:
            bpy.utils.unregister_class(old)
        bpy.utils.register_class(cls)

register()
