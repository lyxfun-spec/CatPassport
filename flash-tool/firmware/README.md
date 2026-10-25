# 固件归档

此目录应恰好包含一个以完整镜像 SHA-256 命名的固件归档。归档来自 `firmware/tools/validate.sh` 产生的 `firmware/build/firmware/<sha256>/`，并由 `firmware/tools/archive_firmware.py verify` 验证。

运行时会再次检查清单、目标芯片、Flash 容量、项目名、ESP-IDF 版本、所有文件大小和 SHA-256。
