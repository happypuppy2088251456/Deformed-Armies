"""
用 Blender 以无界面方式生成障碍物模型并导出 FBX，供 Unity 随机生成使用。

运行方式（由 DSH 调用，不需要打开 Blender 界面）：
    blender.exe --background --factory-startup --python build_obstacles.py

产物：
    <project>/Assets/Arts/Obstacles/Obstacle_Crate.fbx    木箱
    <project>/Assets/Arts/Obstacles/Obstacle_Barrier.fbx  水泥隔离墩
    <project>/Assets/Arts/Obstacles/Obstacle_Rock.fbx     岩石

约定（很重要）：
  * 所有模型的底面都精确坐在 z = 0 上，几何中心在 x/y 的原点。
    这样 Unity 里把物体放在地面高度即可，不需要在代码里补偏移。
  * 单位按 1 Blender 单位 = 1 米。导出用 global_scale=1.0，
    Unity 的 FBX 导入器按厘米解析，配合 meta 里的 globalScale: 1 正好得到 1:1。
  * 每个模型只挂 1~2 个材质，颜色靠 Blender 的材质基础色随 FBX 带过去（不走贴图）。
"""

import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

# ==================== 输出路径 ====================

PROJECT_ROOT = r"D:\分组作业\Deformed-Armies-main"

# 必须直接导出到 Unity 的 Resources 目录下！
#
# 踩过的坑：这里原来写的是 "Assets/Arts/Obstacles"（少了一层 Resources/Obstacles），
# 结果 FBX 被写到 Resources 之外，Unity 的 Resources.Load 根本找不到，
# 于是一直在用旧文件——表现出来就是"模型怎么改都还是那么小"。
# 改这个路径时务必和 Unity 里 ObstacleGenerator 的 modelsResourceFolder 保持一致。
OUT_DIR = os.path.join(PROJECT_ROOT, "Assets", "Arts", "Obstacles", "Resources", "Obstacles")

# 模型几何体的放大倍率。
#
# ============ 尺寸换算的完整公式（改任何一项前先读这里）============
#
#     Unity 里的最终尺寸 = (FBX 里的原始数值) x (0.01) x (Model 导入设置的 Scale Factor)
#
#   其中 0.01 是 Unity 强制行为：它把 FBX 顶点坐标当成厘米解析，一定会除以 100。
#   这个除法不受 .meta 里 useFileUnits / useFileScale 控制（已实验验证）。
#
#   所以有两种配法，二选一，【不要同时用】：
#
#   方案 A：几何体保持写实尺寸，靠 Scale Factor 补偿
#       MODEL_SCALE = 1.0        Scale Factor = 165
#       木箱 1.212 x 0.01 x 165 = 2.00 米
#
#   方案 B（当前采用）：把补偿烘进几何体，Scale Factor 保持 1
#       MODEL_SCALE = 165         Scale Factor = 1
#       木箱 200.0 x 0.01 x 1   = 2.00 米
#
#   为什么选 B：Scale Factor 留在 1，导入设置保持"原样"，所有缩放都写在模型里，
#   在 Unity 里改大小只需要重新跑这个脚本，不需要动导入设置。
#
#   ⚠️ 两边同时放大就会叠乘：曾出现 1.212 x 7 x 100 = 848 米（比 300 米的地图还大）。
#
# ==============================================================
MODEL_SCALE = 165.0

# ==================== 通用工具 ====================


def reset_scene():
    """清空场景，确保每次运行结果一致。"""
    bpy.ops.wm.read_factory_settings(use_empty=True)


def make_material(name, base_color, roughness=0.8, metallic=0.0):
    """创建一个 Principled 材质；基础色会随 FBX 带到 Unity 的 DiffuseColor。"""
    mat = bpy.data.materials.new(name=name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*base_color, 1.0)
        if "Roughness" in bsdf.inputs:
            bsdf.inputs["Roughness"].default_value = roughness
        if "Metallic" in bsdf.inputs:
            bsdf.inputs["Metallic"].default_value = metallic
    # 视口颜色也设上，方便在 Blender 里直接看
    mat.diffuse_color = (*base_color, 1.0)
    return mat


def new_bmesh_object(name):
    """建一个空 mesh 对象并返回 (对象, bmesh)。"""
    mesh = bpy.data.meshes.new(name + "_mesh")
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj, bmesh.new()


def finish_object(obj, bm, materials):
    """把 bmesh 写回对象，赋材质，并按 bmesh 的材质索引生效。"""
    for mat in materials:
        obj.data.materials.append(mat)

    bm.normal_update()
    bm.to_mesh(obj.data)
    bm.free()

    # 平滑着色会让低模看起来更圆润；但方块类要保持硬边，
    # 这里统一用平面着色（默认），细节靠倒角体现。
    for poly in obj.data.polygons:
        poly.use_smooth = False

    obj.data.update()


# --- 几何辅助 ---------------------------------------------------------------


def bm_box(bm, center, size, mat_index=0, bevel=0.0, bevel_segments=2):
    """在 bm 里加一个长方体（中心点 + 尺寸）。返回新建的面列表。"""
    cx, cy, cz = center
    sx, sy, sz = size

    verts = []
    for dz in (-0.5, 0.5):
        for dy in (-0.5, 0.5):
            for dx in (-0.5, 0.5):
                verts.append(
                    bm.verts.new((cx + dx * sx, cy + dy * sy, cz + dz * sz))
                )

    # 顶点顺序: 0(-,-,-) 1(+,-,-) 2(-,+,-) 3(+,+,-) 4(-,-,+) 5(+,-,+) 6(-,+,+) 7(+,+,+)
    faces_idx = [
        (0, 2, 3, 1),  # 底
        (4, 5, 7, 6),  # 顶
        (0, 1, 5, 4),  # 前 (-y)
        (2, 6, 7, 3),  # 后 (+y)
        (0, 4, 6, 2),  # 左 (-x)
        (1, 3, 7, 5),  # 右 (+x)
    ]

    new_faces = []
    for fi in faces_idx:
        f = bm.faces.new([verts[i] for i in fi])
        f.material_index = mat_index
        new_faces.append(f)

    bm.normal_update()

    if bevel > 0.0:
        # 收集要倒角的几何元素时必须去重：
        # 相邻的面共用边和顶点，直接 extend 会把同一元素传多次，
        # bmesh.ops.bevel 会直接报 "found the same (BMVert/BMEdge/BMFace) used multiple times"。
        geom = {}
        for f in new_faces:
            geom[f] = None
            for e in f.edges:
                geom[e] = None
            for v in f.verts:
                geom[v] = None

        bmesh.ops.bevel(
            bm,
            geom=list(geom.keys()),
            offset=bevel,
            segments=bevel_segments,
            profile=0.5,
            affect="EDGES",
            clamp_overlap=True,
        )

    return new_faces


def bm_cylinder(bm, center, radius, depth, verts_count=16, mat_index=0, cap=True):
    """在 bm 里加一个圆柱（轴向为 Z）。"""
    ret = bmesh.ops.create_cone(
        bm,
        cap_ends=cap,
        cap_tris=False,
        segments=verts_count,
        radius1=radius,
        radius2=radius,
        depth=depth,
    )
    new_verts = ret["verts"]
    bmesh.ops.translate(bm, verts=new_verts, vec=Vector(center))

    # 给新面指定材质：取包含新顶点的面
    vset = set(new_verts)
    for f in bm.faces:
        if any(v in vset for v in f.verts):
            f.material_index = mat_index

    return new_verts


# ==================== 三个障碍物造型 ====================


def build_crate():
    """
    木箱：主体 + 四角立柱 + 顶底加强带。
    体积约 1.2 x 1.2 x 1.2 米，底面坐在 z=0。
    """
    wood = make_material("Crate_Wood", (0.55, 0.36, 0.18), roughness=0.85)
    band = make_material("Crate_Band", (0.28, 0.30, 0.33), roughness=0.45, metallic=0.6)

    obj, bm = new_bmesh_object("Obstacle_Crate")

    size = 1.2
    half = size / 2.0

    # 主体
    bm_box(bm, (0.0, 0.0, half), (size, size, size), mat_index=0, bevel=0.03)

    # 四角立柱（金属）
    post = 0.12
    inset = half - post / 2.0
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            bm_box(
                bm,
                (sx * inset, sy * inset, half),
                (post, post, size * 1.005),
                mat_index=1,
                bevel=0.012,
            )

    # 上下加强带（金属），稍微外凸一点
    band_h = 0.12
    for z in (band_h / 2.0 + 0.02, size - band_h / 2.0 - 0.02):
        bm_box(bm, (0.0, 0.0, z), (size * 1.01, size * 1.01, band_h), mat_index=1, bevel=0.012)

    finish_object(obj, bm, [wood, band])
    return obj


def build_barrier():
    """
    水泥隔离墩：下宽上窄的梯形墩身 + 两条警示条。
    约 2.0 x 0.6 x 1.0 米，底面坐在 z=0。
    """
    concrete = make_material("Barrier_Concrete", (0.62, 0.62, 0.60), roughness=0.9)
    stripe = make_material("Barrier_Stripe", (0.85, 0.45, 0.05), roughness=0.5)

    obj, bm = new_bmesh_object("Obstacle_Barrier")

    length = 2.0
    base_w = 0.6
    top_w = 0.32
    height = 1.0

    # 用 8 个顶点直接建梯形棱柱
    hx = length / 2.0
    hb = base_w / 2.0
    ht = top_w / 2.0

    coords = [
        (-hx, -hb, 0.0),
        (hx, -hb, 0.0),
        (hx, hb, 0.0),
        (-hx, hb, 0.0),
        (-hx, -ht, height),
        (hx, -ht, height),
        (hx, ht, height),
        (-hx, ht, height),
    ]
    verts = [bm.verts.new(c) for c in coords]

    faces_idx = [
        (0, 1, 2, 3),  # 底
        (4, 7, 6, 5),  # 顶
        (0, 4, 5, 1),  # 前
        (1, 5, 6, 2),  # 右
        (2, 6, 7, 3),  # 后
        (3, 7, 4, 0),  # 左
    ]
    for fi in faces_idx:
        f = bm.faces.new([verts[i] for i in fi])
        f.material_index = 0

    bm.normal_update()

    # 两条警示条：贴在墩身两侧的斜面上，用薄盒子近似
    for z in (0.30, 0.62):
        w = base_w + (top_w - base_w) * (z / height)
        bm_box(bm, (0.0, 0.0, z), (length * 0.99, w * 1.03, 0.12), mat_index=1, bevel=0.006)

    finish_object(obj, bm, [concrete, stripe])
    return obj


def build_rock():
    """
    岩石：低模不规则石块，靠顶点扰动做出棱角。
    约 1.6 x 1.4 x 1.1 米，底面坐在 z=0。
    """
    stone = make_material("Rock_Stone", (0.45, 0.44, 0.42), roughness=0.95)

    obj, bm = new_bmesh_object("Obstacle_Rock")

    # 从一个细分过的立方体开始，做出更像石头的基础形
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.subdivide_edges(bm, edges=list(bm.edges), cuts=2, use_grid_fill=True)

    # 用确定性的伪随机扰动顶点，避免每次运行结果不同
    seed = 20260906

    def det_rand(i, salt):
        x = (i * 1103515245 + salt * 12345 + seed) & 0x7FFFFFFF
        return ((x >> 8) % 10000) / 10000.0

    for i, v in enumerate(bm.verts):
        n = v.co.normalized()
        # 沿法线方向做有节奏的扰动
        amount = 0.10 + 0.16 * det_rand(i, 1)
        v.co += n * amount
        # 再加一点各向异性拉伸，让石头不至于像球
        v.co.x *= 1.15
        v.co.y *= 1.0
        v.co.z *= 0.78

    bm.normal_update()

    # 压扁 + 抬高，使底面贴在 z=0
    zs = [v.co.z for v in bm.verts]
    zmin, zmax = min(zs), max(zs)
    height = zmax - zmin
    for v in bm.verts:
        v.co.z = (v.co.z - zmin) * (1.1 / height)  # 统一高度到 1.1 米

    # 整体缩放到目标占地面积
    xs = [v.co.x for v in bm.verts]
    ys = [v.co.y for v in bm.verts]
    span_x = max(xs) - min(xs)
    span_y = max(ys) - min(ys)
    for v in bm.verts:
        v.co.x *= 1.6 / span_x
        v.co.y *= 1.4 / span_y

    for f in bm.faces:
        f.material_index = 0

    finish_object(obj, bm, [stone])
    return obj


# ==================== 导出 ====================


def export_fbx(obj, filepath):
    """只导出指定对象为 FBX。"""
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    bpy.ops.export_scene.fbx(
        filepath=filepath,
        use_selection=True,
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_NONE",
        axis_forward="-Z",
        axis_up="Y",
        object_types={"MESH"},
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=False,
        path_mode="COPY",
        embed_textures=False,
        bake_space_transform=False,
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        use_custom_props=False,
    )


def report_bounds(obj):
    """打印模型的真实世界包围盒，便于核对尺寸。"""
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    print(
        "BOUNDS {} x={:.3f}..{:.3f} y={:.3f}..{:.3f} z={:.3f}..{:.3f} verts={} polys={}".format(
            obj.name,
            min(xs), max(xs),
            min(ys), max(ys),
            min(zs), max(zs),
            len(obj.data.vertices),
            len(obj.data.polygons),
        )
    )


def normalize_to_ground(obj, scale=1.0):
    """
    把模型整体缩放 scale 倍，并摆正：底面最低点贴到 z=0，水平方向按包围盒中心对齐到 x=0 / y=0。

    为什么需要：
      * 倒角等操作会把顶点推到 z 略小于 0 的位置（例如 -0.003），
        那样物体在 Unity 里就会陷进地面一点点；
      * 放大后还想要精确贴地，所以两件事一起做，规则简单可预测。
    """
    verts = obj.data.vertices
    if len(verts) == 0:
        return

    xs = [v.co.x for v in verts]
    ys = [v.co.y for v in verts]
    zs = [v.co.z for v in verts]

    cx = (min(xs) + max(xs)) / 2.0
    cy = (min(ys) + max(ys)) / 2.0
    zmin = min(zs)

    for v in verts:
        v.co.x = (v.co.x - cx) * scale
        v.co.y = (v.co.y - cy) * scale
        v.co.z = (v.co.z - zmin) * scale

    obj.data.update()


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    builders = [
        ("Obstacle_Crate", build_crate),
        ("Obstacle_Barrier", build_barrier),
        ("Obstacle_Rock", build_rock),
    ]

    for name, builder in builders:
        reset_scene()
        obj = builder()

        if len(obj.data.vertices) == 0:
            print("ERROR {} 没有生成任何几何体".format(name))
            continue

        # 摆正 + 按 MODEL_SCALE 整体放大
        normalize_to_ground(obj, MODEL_SCALE)

        # 岩石本来被单独压扁/拉伸过，放大后仍然保持它自己的比例；
        # 但如果它此前用"压到 1.1 米高"的方式定尺寸，这里会跟着一起放大，符合预期。
        report_bounds(obj)

        fbx_path = os.path.join(OUT_DIR, name + ".fbx")
        export_fbx(obj, fbx_path)

        if os.path.exists(fbx_path):
            print("EXPORTED {} size={} bytes".format(fbx_path, os.path.getsize(fbx_path)))
        else:
            print("ERROR 导出失败 {}".format(fbx_path))

    print("DONE")


if __name__ == "__main__":
    main()
