// Configuración del proyecto Gradle: repositorios de los complementos y de las dependencias, y el único módulo «app».
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    // Todas las dependencias se resuelven aquí; un módulo no puede declarar repositorios propios.
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}
rootProject.name = "SignalTrace"
include(":app")
