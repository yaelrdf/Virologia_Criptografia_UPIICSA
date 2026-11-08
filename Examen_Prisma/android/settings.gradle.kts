// Configuración de Gradle de la app Android de ABCO: repositorios de plugins y dependencias, y módulos.

// Repositorios de donde Gradle descarga los plugins (Android Gradle Plugin, Kotlin).
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
// Repositorios de las bibliotecas; FAIL_ON_PROJECT_REPOS impide que un módulo declare los suyos.
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}
// El nombre interno del proyecto conserva el nombre anterior (PRISMA-L); la app visible se llama ABCO.
rootProject.name = "PrismaL"
include(":app")
