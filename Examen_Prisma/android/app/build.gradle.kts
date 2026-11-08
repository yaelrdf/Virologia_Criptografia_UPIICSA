// Script de Gradle del módulo :app (cliente Android de ABCO): SDK, URL del servidor por defecto,
// firma release opcional y dependencias (Compose, Material 3, corrutinas, OkHttp, kotlinx.serialization).
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// URL del servidor por defecto: ./gradlew assembleRelease -PserverUrl=https://midominio
// Sin la propiedad se usa http://10.0.2.2:5080, la dirección del equipo anfitrión vista desde el emulador.
// Es solo el valor inicial: el usuario puede cambiarla desde la app (AppPrefs).
val serverUrl: String = (project.findProperty("serverUrl") as String?)
    ?.trim()?.trimEnd('/')
    ?.takeIf { it.isNotEmpty() }
    ?: "http://10.0.2.2:5080"

// Firma release: se lee de keystore.properties (no se versiona) si existe.
// Sin ese archivo el APK release se genera sin firmar.
val keystorePropsFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply {
    if (keystorePropsFile.exists()) keystorePropsFile.inputStream().use { load(it) }
}
val hasReleaseKey = keystorePropsFile.exists()

android {
    // El paquete conserva el nombre "prisma" para que el APK se instale encima de versiones anteriores.
    namespace = "mx.ipn.upiicsa.prisma"
    compileSdk = 37

    defaultConfig {
        applicationId = "mx.ipn.upiicsa.prisma"
        minSdk = 26
        targetSdk = 37
        versionCode = 1
        versionName = "1.0.0"
        // Expone la URL por defecto como BuildConfig.DEFAULT_SERVER_URL.
        buildConfigField("String", "DEFAULT_SERVER_URL", "\"$serverUrl\"")
    }

    signingConfigs {
        if (hasReleaseKey) {
            create("release") {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            // Sin R8/ProGuard: no se ofusca ni se reduce el código del release.
            isMinifyEnabled = false
            if (hasReleaseKey) signingConfig = signingConfigs.getByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

dependencies {
    // Jetpack Compose (versiones alineadas por el BOM) y Material 3.
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    // Corrutinas, JSON y cliente HTTP para hablar con la API REST del servidor.
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.okhttp)

    // Prueba de integración contra un servidor real (ApiClientIntegrationTest).
    testImplementation(libs.junit)
}
