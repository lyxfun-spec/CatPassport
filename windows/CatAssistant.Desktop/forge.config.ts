import type { ForgeConfig } from '@electron-forge/shared-types';
import { MakerSquirrel } from '@electron-forge/maker-squirrel';
import { MakerZIP } from '@electron-forge/maker-zip';
import { VitePlugin } from '@electron-forge/plugin-vite';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const desktopDirectory = path.dirname(fileURLToPath(import.meta.url));
const backendProject = path.resolve(desktopDirectory, '..', 'CatAssistant.Backend', 'CatAssistant.Backend.csproj');
const backendOutput = path.resolve(desktopDirectory, '.artifacts', 'backend');
const iconPath = path.resolve(desktopDirectory, '..', '..', 'assets', 'branding', 'cat-assistant-icon-transparent.png');

const config: ForgeConfig = {
  packagerConfig: {
    asar: true,
    executableName: 'CatAssistant',
    extraResource: [backendOutput, iconPath],
    download: {
      // Pin the official Electron release checksums so packaging remains verifiable
      // when GitHub's checksum endpoint is unavailable from the build environment.
      checksums: {
        'electron-v44.7.0-win32-arm64.zip': 'ee2621a4513f92a952273d5363e153db6f0e4dd183d89f2c0f740faa13623316',
        'electron-v44.7.0-win32-x64.zip': 'eee30dc8fa1f5ea95490e59f44e46ea68dd24c6e93d22facf70fe5c2d4c2665c'
      }
    }
  },
  rebuildConfig: {},
  hooks: {
    prePackage: async (_config, platform, arch) => {
      if (platform !== 'win32') throw new Error('小猫助手桌面版目前只支持 Windows 打包。');
      const rid = arch === 'arm64' ? 'win-arm64' : 'win-x64';
      execFileSync('dotnet', ['publish', backendProject, '-c', 'Release', '-r', rid, '--self-contained', 'true', '-o', backendOutput], { stdio: 'inherit' });
    }
  },
  makers: [
    new MakerSquirrel({ name: 'CatAssistant', setupExe: 'CatAssistant-Setup.exe' }),
    new MakerZIP({}, ['win32'])
  ],
  plugins: [
    new VitePlugin({
      build: [
        { entry: 'src/main.ts', config: 'vite.main.config.ts', target: 'main' },
        { entry: 'src/preload.ts', config: 'vite.preload.config.ts', target: 'preload' }
      ],
      renderer: [{ name: 'main_window', config: 'vite.renderer.config.ts' }]
    })
  ]
};

export default config;
