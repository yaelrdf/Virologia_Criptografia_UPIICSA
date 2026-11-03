// Script de Gradle del proyecto raíz de la app Android de ABCO.
// Solo declara los plugins (versiones en gradle/libs.versions.toml) sin aplicarlos; el módulo :app los aplica.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}
