# OBB 性能优化报告

## 1. 问题分级

### P0 致命问题
- `OBB` 主命令默认走旧版高成本链路：`Solid3d.Explode -> 曲线采样 -> 多轮 OBB 候选`，复杂板件容易明显卡顿。
- `OutlineService -> FlatshotProjectionService` 对同一实体存在重复克隆，导致异形板精修路径重复付出大对象复制成本。

### P1 严重问题
- `FLATSHOT` 对圆弧和样条使用固定高密采样，长板件或复杂侧边会生成过多边段，拖慢轮廓链接。
- 同一板件反复执行 `OBB` 时没有缓存，重复支付几何分析成本。

### P2 优化问题
- 旧版 `OBB` 的角度穷举、O(n^2) 去重点集仍保留在回退路径中，虽然不再是主路径，但后续仍建议继续替换。

## 2. 本轮实施方案

### 改进项 A
- 目标：给 `OBB` 命令切换快速主路径。
- 实现：新增 `ObbComputationService`，优先采用 `克隆体 + BZBJ 摆正 + GeometricExtents` 直接计算尺寸。
- 依赖：`BzbjAlignmentService.AlignToWorldXY()`。
- 验收：主路径成功时不再进入旧版 `Explode` 重路径；编译通过。

### 改进项 B
- 目标：减少重复几何对象克隆。
- 实现：`FlatshotProjectionService` 新增 `ProjectAligned()`，供 `OutlineService` 在已摆正克隆体上直接投影。
- 依赖：`OutlineService.TryFlatshotProjection()`。
- 验收：`OutlineService` 不再对同一克隆体再次调用内部克隆版投影入口。

### 改进项 C
- 目标：降低复杂曲线边界的边段膨胀。
- 实现：将圆弧、圆、椭圆、样条的固定采样改为按曲线长度自适应采样，保留上限保护。
- 依赖：`FlatshotProjectionService.FlattenCurvesToEdges()`。
- 验收：长曲线样本数随长度变化，短曲线不再使用过高采样密度。

### 改进项 D
- 目标：避免同一板件重复计算 OBB。
- 实现：`ObbComputationService` 增加基于 `Handle + Extents + Volume` 的内存缓存。
- 依赖：`Solid3d.MassProperties`、`GeometricExtents`。
- 验收：相同几何签名的板件二次计算直接命中缓存。

### 改进项 E
- 目标：降低旧版回退链中点集去重的平方级开销。
- 实现：将 `MyPlugin` 中的 `RemoveDuplicateVertices()` 与 `RemoveDuplicatePoints2D()` 改为哈希量化去重。
- 依赖：`MyPlugin.CalculateOBBFromRealVertices()`、`CalculateOBBForPlane()`。
- 验收：去重逻辑不再执行双重循环比较，编译与回归通过。

### 改进项 F
- 目标：减少最小包围矩形搜索的无效候选角。
- 实现：`FindMinimumBoundingRectangleImproved()` 不再额外扫描 180 个均匀角度，只保留凸包边方向及其正交方向作为候选角，并做归一化去重。
- 依赖：`ComputeConvexHull2D()`、`CalculateBoundingRectangleAtAngle()`。
- 验收：矩形搜索候选数量显著下降，外部尺寸结果保持可用。

### 改进项 G
- 目标：进一步降低最小包围矩形求解时的整批点旋转与重复扫描成本。
- 实现：`FindMinimumBoundingRectangleImproved()` 已切换为旋转卡尺式的支撑点推进逻辑，沿凸包边方向单调更新四个支撑点。
- 依赖：`FindMinimumBoundingRectangleByRotatingCalipers()`、`AdvanceMaxSupportIndex()`。
- 验收：编译通过，最小包围矩形主路径不再依赖候选角整批旋转。

### 改进项 H
- 目标：为真实图纸中的 OBB 慢点定位提供可视化分段耗时。
- 实现：新增 `ObbPerformanceTrace`，并在 `ObbComputationService`、`MyPlugin.CalculateOBBDimensionsFromSolid()` 与 `OBB` 命令输出中接入轻量级计时。
- 依赖：`ObbPerformanceTrace.cs`、`OBB` 命令入口。
- 验收：执行 `OBB` 命令时，CAD 命令行可看到 `cache_key / cache_lookup / fast.align_xy / fallback.*` 等分段耗时。

## 3. 已修改文件

- `ObbComputationService.cs`
- `MyPlugin.cs`
- `FlatshotProjectionService.cs`
- `OutlineService.cs`
- `ObbPerformanceTrace.cs`
- `OBB性能优化报告.md`

## 4. 验证结果

### 单项验证
- `OBB` 快速主路径：已接入 `MyPlugin.CalculateOBBDimensionsFromSolid()`。
- 重复克隆消除：`OutlineService` 已改为调用 `FlatshotProjectionService.ProjectAligned()`。
- 自适应采样：`FlatshotProjectionService` 已替换固定高密采样。
- 缓存：`ObbComputationService` 已启用 10 分钟 TTL 的结果缓存。
- 哈希去重：`MyPlugin` 的 2D/3D 点去重已替换为哈希量化去重。
- 候选角收敛：最小包围矩形已取消 180 角度穷举，改为仅按凸包边方向评估。
- 旋转卡尺推进：最小包围矩形主路径已改为支撑点单调推进，减少整批点旋转和临时集合创建。
- 分段耗时输出：`OBB` 命令已可输出快速路径、回退路径及 FLATSHOT 精修入口的阶段耗时。

### 集成验证
- `GetDiagnostics`：相关文件诊断为空。
- `dotnet build FurniturePlugin.csproj`：通过，`0` 警告，`0` 错误。

## 5. 风险控制

- 所有改动均采用“快速主路径优先，旧路径保底”的方式，避免影响现有模块的兜底能力。
- 没有删除旧版复杂算法，仅降低其触发概率。
- `OutlineService` 的外部接口未破坏，只新增 `ProjectAligned()` 分流。

## 6. 本轮结论

- 已完成 P0 和进一步的 P1/P2 优化。
- 当前最重的 `OBB` 命令路径已经从“默认全量重算”切换为“优先快速摆正路径 + 缓存复用”，回退链中的去重、候选角搜索和最小包围矩形主求解也已继续收敛。
- 后续若继续深挖，可优先补充独立的纯算法性能测试工程，并基于现有分段耗时输出继续压缩最慢阶段。
