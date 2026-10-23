import { defineConfig } from 'vitepress'

const repo = 'https://github.com/zhengnan365/DeskHoper'

export default defineConfig({
  lang: 'zh-CN',
  title: 'DeskHoper',
  description: '一台台式机和一台笔记本共用同一套外接显示器、键盘和鼠标。屏幕切换、文件共享、键鼠共享，全部局域网直连。',
  head: [
    ['link', { rel: 'icon', type: 'image/png', href: '/favicon.png' }],
    ['meta', { name: 'theme-color', content: '#2f6fed' }],
  ],
  cleanUrls: true,
  lastUpdated: false,
  themeConfig: {
    logo: '/favicon.png',
    siteTitle: 'DeskHoper',
    nav: [
      { text: '首页', link: '/' },
      { text: '功能特性', link: '/features' },
      { text: '快速开始', link: '/start' },
      { text: '网络与安全', link: '/network' },
      { text: '常见问题', link: '/faq' },
      { text: '更新日志', link: '/changelog' },
    ],
    aside: false,
    outline: false,
    socialLinks: [{ icon: 'github', link: repo }],
    search: { provider: 'local' },
    docFooter: { prev: '上一页', next: '下一页' },
    darkModeSwitchLabel: '外观',
    lightModeSwitchTitle: '切换到浅色模式',
    darkModeSwitchTitle: '切换到深色模式',
    sidebarMenuLabel: '目录',
    returnToTopLabel: '回到顶部',
    footer: {
      message: '基于 MIT 协议开源，代码公开可审计。',
      copyright: '郑在探索 · zhengnan.me@gmail.com',
    },
  },
  vite: {
    server: { host: '127.0.0.1' },
  },
})
