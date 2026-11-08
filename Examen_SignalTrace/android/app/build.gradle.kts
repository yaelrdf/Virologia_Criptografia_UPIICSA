// Compilación del módulo de la aplicación Android: SDK, firma de la versión release, Jetpack Compose y dependencias.
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// URL del servidor que trae la APK por defecto: ./gradlew assembleRelease -PserverUrl=https://mi-tunel
// Sin la propiedad se usa 10.0.2.2, que en el emulador de Android apunta al equipo anfitrión.
val serverUrl: String = (project.findProperty("serverUrl") as String?)
    ?.trim()?.trimEnd('/')
    ?.takeIf { it.isNotEmpty() }
    ?: "http://10.0.2.2:5090"

// Firma de la versión release: se lee de keystore.properties (nunca se sube al repositorio) cuando existe.
val keystorePropsFile = rootProject.file("keystore.properties")
val keystoreProps = Properties().apply {
    if (keystorePropsFile.exists()) keystorePropsFile.inputStream().use { load(it) }
}
val hasReleaseKey = keystorePropsFile.exists()

android {
    namespace = "mx.ipn.upiicsa.signaltrace"
    compileSdk = 37

    defaultConfig {
        applicationId = "mx.ipn.upiicsa.signaltrace"
        // Android 8.0 como mínimo.
        minSdk = 26
        targetSdk = 37
        versionCode = 2
        versionName = "2.0.0"
        // Queda disponible en el código como BuildConfig.DEFAULT_SERVER_URL.
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
            // Sin ofuscación, para que el código de la APK se pueda revisar.
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

    testOptions {
        unitTests.all {
            // Las pruebas en la JVM leen reference/test-vectors.json y, opcionalmente, hablan con un servidor en ejecución.
            it.systemProperty("st.vectors", rootProject.file("../reference/test-vectors.json").absolutePath)
            System.getenv("ST_TEST_SERVER")?.let { url -> it.environment("ST_TEST_SERVER", url) }
            System.getenv("ST_TEST_PASSWORD")?.let { pw -> it.environment("ST_TEST_PASSWORD", pw) }
        }
    }
}

dependencies {
    // Interfaz con Jetpack Compose y Material 3.
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons.core)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    // Corrutinas, JSON, cliente HTTP y lector de QR.
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.okhttp)
    implementation(libs.zxing.embedded)

    testImplementation(libs.junit)
}
