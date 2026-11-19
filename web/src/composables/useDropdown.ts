import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue';

/**
 * 一个下拉面板要用的东西：展开状态 + 组件根节点。
 */
export interface Dropdown {
  /** 面板是否展开。 */
  open: Ref<boolean>;
  /** 组件根节点：用来判断「点的是不是组件内部」。 */
  root: Ref<HTMLElement | null>;
}

/**
 * 下拉面板的公共逻辑：点组件外面收起、Esc 收起。
 *
 * 这段逻辑原先在 TagFilter / ProjectDropdown / MoreMenu 里各抄了一份
 * （逐字相同）。再抄一份就是四处各改各的 —— 漏一处就是一个组件关不掉面板，
 * 而「面板关不掉」是用户一眼就能看出来的坏味道。
 *
 * 用法：
 *
 * ```ts
 * const root = ref<HTMLElement | null>(null);
 * const { open } = useDropdown(root);
 * ```
 *
 * 模板里把 `root` 绑到**同时包含触发器与面板**的那个元素上：
 * 点面板里的项也算「点组件内部」，不该把面板关掉。
 *
 * ⚠️ 根节点由调用方声明并传进来（不传就自己建一个）：组件里它要绑到
 * 模板的 `ref="…"` 上，而 `ref="…"` 是字符串形式 —— 编译器不把它算作
 * 对变量的**读取**，只声明不读会被 `noUnusedLocals` 判成「未使用」。
 * 传进来时它就是被读过的（实参），不会踩那一条。
 *
 * @param root 组件根节点（模板里 `ref="…"` 绑的那个元素）。
 */
export function useDropdown(root: Ref<HTMLElement | null> = ref<HTMLElement | null>(null)): Dropdown {
  /** 面板是否展开。 */
  const open = ref(false);

  /**
   * 点组件外面 → 收起面板。
   *
   * 面板是浮在下拉按钮下面的一层，不点它就不会消失的话，
   * 用户会以为「点了没反应」，只能刷新页面。
   *
   * 点在组件内部（按钮本身、面板里的复选框）不算「外面」——
   * 触发按钮的 click 会冒泡到 document，靠这条判断才不会「刚打开就被关掉」。
   */
  function onDocumentClick(event: MouseEvent): void {
    if (!open.value) {
      return;
    }
    const target = event.target;
    if (target instanceof Node && root.value?.contains(target)) {
      return;
    }
    open.value = false;
  }

  /** Esc → 收起面板。 */
  function onDocumentKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      open.value = false;
    }
  }

  onMounted(() => {
    document.addEventListener('click', onDocumentClick);
    document.addEventListener('keydown', onDocumentKeydown);
  });

  onBeforeUnmount(() => {
    document.removeEventListener('click', onDocumentClick);
    document.removeEventListener('keydown', onDocumentKeydown);
  });

  return { open, root };
}
