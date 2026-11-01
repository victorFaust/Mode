pipeline {
    // The agent needs the .NET 10 SDK, Python 3.11, Node 22 and Docker (Testcontainers starts PostgreSQL for the API tests).
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
    }

    environment {
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }

    stages {
        stage('API: build and test') {
            steps {
                sh 'dotnet restore Mode.sln'
                sh 'dotnet build Mode.sln -c Release --no-restore'
                sh 'dotnet test Mode.sln -c Release --no-build --logger trx --results-directory TestResults'
            }
        }

        stage('File processor: test') {
            steps {
                dir('services/file-processor') {
                    sh '''
                        python3 -m venv .venv
                        . .venv/bin/activate
                        pip install -r requirements-dev.txt
                        pytest -q --junitxml="$WORKSPACE/TestResults/pytest.xml"
                    '''
                }
            }
        }

        stage('Client: test and build') {
            steps {
                dir('client') {
                    sh 'npm ci'
                    sh 'npm test'
                    sh 'npm run build'
                }
            }
        }
    }

    post {
        always {
            junit allowEmptyResults: true, testResults: 'TestResults/*.xml'
            archiveArtifacts allowEmptyArchive: true, artifacts: 'TestResults/**'
        }
    }
}
