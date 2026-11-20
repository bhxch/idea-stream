<script setup lang="ts">
import ToastHost from './components/ToastHost.vue';
</script>

<template>
  <!--
    跳到主内容：键盘用户按一次 Tab 就能跳过侧栏、顶栏那些控件，直接进主内容。

    平时藏在屏幕外（见 styles/base.css 的 .skip-link），聚焦时露出来。
    放在 RouterView **前面** —— 它得是页面里第一个能聚焦的东西。
  -->
  <a class="skip-link" href="#main-content">跳到主内容</a>
  <RouterView />
  <ToastHost />
</template>
