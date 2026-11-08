// Archivo de compilación raíz: declara los complementos (versiones en gradle/libs.versions.toml) sin aplicarlos;
// los aplica el módulo app.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}
